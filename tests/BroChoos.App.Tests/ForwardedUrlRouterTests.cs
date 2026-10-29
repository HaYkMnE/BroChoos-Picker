using System.Collections.Concurrent;
using BroChoos.App.Picker;
using BroChoos.Core.Abstractions;
using BroChoos.Core.Ingress;
using BroChoos.Platform.Windows.SingleInstance;
using BroChoos.Platform.Windows.Tests.SingleInstance;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BroChoos.App.Tests;

// Findings A1 + A3 pinned over the REAL forward path: each test runs a production
// primary guard with the router attached exactly like Program does, then forwards
// a URL from a second guard over the actual named pipe. This proves the fallback
// contract where it now SOLELY lives (A1 removed the guard's own echo branch) and
// that a repeat Attach cannot multiply delivery (A3). The class joins the serial
// console-capture collection because it redirects Console.Out and because the
// guard's Global mutex name is per-user (two primaries at once would collide).
//
// THREADING MODEL (observed flake pinned here): a Windows mutex is owned PER
// THREAD and re-entrant for its owner, and an owning pool thread freed at an
// await can be REUSED by Task.Run — the secondary's WaitOne(0) then succeeds
// recursively and misreports Primary (seen once in Release). So every guard
// acquisition below runs on a freshly created Thread: it can never be the owner
// thread, and the primary additionally releases on its OWN owner thread at
// teardown (the guard's Dispose swallows a wrong-thread ReleaseMutex).
[Collection(ConsoleCaptureCollection.Name)]
public sealed class ForwardedUrlRouterTests
{
	private const string ForwardedUrl = "https://forwarded.example/path";
	private const string RedactedEcho = "[primary received] forwarded activation buffered";

	// A1 pin: a sessionless primary (the --no-ui case: Program always attaches the
	// router, and no session ever activates on that path) surfaces the forward
	// EXACTLY ONCE through the router's echo, and that same forward is buffered
	// for a session that activates late.
	[SkippableFact]
	public async Task SessionlessPrimary_EchoesForwardOnceThroughTheRouter_AndBuffersForALateSession()
	{
		StringWriter console = CaptureConsole(out TextWriter original);

		try
		{
			using PrimaryGuardHost primary = new("https://primary.example/");
			_ = primary.WaitForRole(TimeSpan.FromSeconds(10)).Should().BeTrue("local mutex work; 10 s is generous");
			_ = primary.Role.Should().Be(SingleInstanceRole.Primary);

			ForwardedUrlRouter router = new(NullLogger<ForwardedUrlRouter>.Instance);
			router.Attach(primary.Guard);

			SingleInstanceRole secondaryRole = ForwardAsSeparateProcessWould(ForwardedUrl);
			_ = secondaryRole.Should().Be(SingleInstanceRole.Secondary);

			bool echoed = await WaitForAsync(() => console.ToString().Contains(RedactedEcho, StringComparison.Ordinal));
			_ = echoed.Should().BeTrue($"the router's fallback echo should surface the forward; console was: {console}");
			_ = Occurrences(console.ToString(), RedactedEcho).Should().Be(1, "the guard's own echo branch is gone: exactly one fallback owner (A1)");
			_ = console.ToString().Should().NotContain(ForwardedUrl, "fallback console output must stay URL-redacted");

			FakePickerTarget lateSession = new();
			router.ActivateSession(lateSession);

			_ = lateSession.Received.Should().Equal(ForwardedUrl);
			_ = Occurrences(console.ToString(), RedactedEcho).Should().Be(1, "flushing the buffered URL to the session must not echo it again");
		}
		finally
		{
			Console.SetOut(original);
		}
	}

	// A3 pin: Attach called twice on the same guard instance subscribes exactly
	// once, so one forwarded URL reaches the active session exactly once.
	[SkippableFact]
	public async Task Attach_CalledTwiceOnTheSameGuard_DeliversEachForwardExactlyOnce()
	{
		StringWriter console = CaptureConsole(out TextWriter original);

		try
		{
			using PrimaryGuardHost primary = new("https://primary.example/");
			_ = primary.WaitForRole(TimeSpan.FromSeconds(10)).Should().BeTrue();
			_ = primary.Role.Should().Be(SingleInstanceRole.Primary);

			ForwardedUrlRouter router = new(NullLogger<ForwardedUrlRouter>.Instance);
			router.Attach(primary.Guard);
			router.Attach(primary.Guard);

			FakePickerTarget activeSession = new();
			router.ActivateSession(activeSession);

			SingleInstanceRole secondaryRole = ForwardAsSeparateProcessWould(ForwardedUrl);
			_ = secondaryRole.Should().Be(SingleInstanceRole.Secondary);

			bool delivered = await WaitForAsync(() => activeSession.Received.Length > 0);
			_ = delivered.Should().BeTrue("the attached router must route the forward to the active session");

			// A double subscription fires twice within the same server-loop iteration,
			// so a short settle makes the multiplication observable before asserting.
			await Task.Delay(300);
			_ = activeSession.Received.Should().Equal(ForwardedUrl);
			_ = Occurrences(console.ToString(), RedactedEcho).Should().Be(0, "with an active session the forward is routed, not echoed");
		}
		finally
		{
			Console.SetOut(original);
		}
	}

	private static WindowsSingleInstanceGuard NewGuard()
	{
		OneShotSecretValidator oneShot = new();
		IpcPeerAuthenticator authenticator = new(
			oneShot,
			new ApprovedTrustPolicyProvider());

		return new WindowsSingleInstanceGuard(NullLogger<WindowsSingleInstanceGuard>.Instance, authenticator, oneShot);
	}

	// One forward exactly as a separate process would run it: a freshly started
	// Thread (never the mutex-owner thread, never pooled) acquires, forwards and
	// reports; the guard instance owns nothing at that point (the createdNew=false
	// path already disposed its own mutex handle before forwarding), so disposing
	// on this thread is safe.
	private static SingleInstanceRole ForwardAsSeparateProcessWould(string url)
	{
		using WindowsSingleInstanceGuard secondary = NewGuard();
		SingleInstanceRole role = SingleInstanceRole.Failed;

		Thread forwarder = new(() =>
		{
			role = secondary.AcquireOrForwardAsync(BuildRequest(url), CancellationToken.None).GetAwaiter().GetResult();
		})
		{ IsBackground = true };

		forwarder.Start();
		_ = forwarder.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("the forward has its own 2 s pipe timeout; 30 s is generous");
		return role;
	}

	private static StringWriter CaptureConsole(out TextWriter original)
	{
		original = Console.Out;
		StringWriter writer = new();
		Console.SetOut(writer);
		return writer;
	}

	private static async Task<bool> WaitForAsync(Func<bool> condition)
	{
		const int timeoutMs = 10_000;
		int waited = 0;

		while (!condition())
		{
			if (waited >= timeoutMs)
			{
				return false;
			}

			await Task.Delay(50);
			waited += 50;
		}

		return true;
	}

	private static int Occurrences(string haystack, string needle)
	{
		int count = 0;
		int index = 0;

		while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
		{
			count++;
			index += needle.Length;
		}

		return count;
	}

	private sealed class FakePickerTarget : IPickerForwardTarget
	{
		private readonly ConcurrentQueue<string> _received = new();

		public string[] Received => [.. _received];

		public void OnForwardedActivation(ActivationRequest request)
		{
			_received.Enqueue(request.NormalizedUrl);
		}
	}

	[SkippableFact]
	public async Task SessionActivatedAfterTwoForwards_ReceivesBothInArrivalOrder()
	{
		StringWriter console = CaptureConsole(out TextWriter original);

		try
		{
			using PrimaryGuardHost primary = new("https://primary.example/");
			_ = primary.WaitForRole(TimeSpan.FromSeconds(10)).Should().BeTrue();
			_ = primary.Role.Should().Be(SingleInstanceRole.Primary);

			ForwardedUrlRouter router = new(NullLogger<ForwardedUrlRouter>.Instance);
			router.Attach(primary.Guard);

			_ = ForwardAsSeparateProcessWould("https://forwarded.example/a").Should().Be(SingleInstanceRole.Secondary);
			_ = ForwardAsSeparateProcessWould("https://forwarded.example/b").Should().Be(SingleInstanceRole.Secondary);

			FakePickerTarget lateSession = new();
			router.ActivateSession(lateSession);

			bool delivered = await WaitForAsync(() => lateSession.Received.Length == 2);
			_ = delivered.Should().BeTrue();
			_ = lateSession.Received.Should().Equal("https://forwarded.example/a", "https://forwarded.example/b");
		}
		finally
		{
			Console.SetOut(original);
		}
	}

	// Ordering race pinned DETERMINISTICALLY (no pipe, no sleeps): two URLs are buffered,
	// the session activates on its own thread and its FIRST delivery blocks on a gate; while
	// it is blocked, a third URL arrives on another thread (the guard's drain loop in
	// production). The late URL must queue behind the buffered ones, never overtake them.
	[Fact]
	public void UrlArrivingWhileBufferedUrlsAreBeingDelivered_IsDeliveredAfterThem_InArrivalOrder()
	{
		ForwardedUrlRouter router = new(NullLogger<ForwardedUrlRouter>.Instance);
		router.OnUrlReceived(null, BuildRequest("https://forwarded.example/a"));
		router.OnUrlReceived(null, BuildRequest("https://forwarded.example/b"));

		using ManualResetEventSlim firstDeliveryEntered = new();
		using ManualResetEventSlim releaseFirstDelivery = new();
		RecordingTarget target = new(onDelivery: index =>
		{
			if (index == 0)
			{
				firstDeliveryEntered.Set();
				_ = releaseFirstDelivery.Wait(TimeSpan.FromSeconds(10));
			}
		});

		Thread activator = new(() => router.ActivateSession(target)) { IsBackground = true };
		activator.Start();
		_ = firstDeliveryEntered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the activation must start delivering the buffered URLs");

		Thread lateForward = new(() => router.OnUrlReceived(null, BuildRequest("https://forwarded.example/c"))) { IsBackground = true };
		lateForward.Start();
		_ = lateForward.Join(TimeSpan.FromSeconds(10)).Should().BeTrue("a forward arriving during delivery must not block on the in-flight delivery");

		releaseFirstDelivery.Set();
		_ = activator.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();

		_ = target.Received.Should().Equal("https://forwarded.example/a", "https://forwarded.example/b", "https://forwarded.example/c");
	}

	// A target that throws must neither lose the URLs behind the failing one nor leave the
	// router wedged in a "delivery in progress" state that swallows every later forward.
	[Fact]
	public void TargetThrowingOnADelivery_DoesNotLoseLaterUrls_AndDoesNotWedgeTheRouter()
	{
		ForwardedUrlRouter router = new(NullLogger<ForwardedUrlRouter>.Instance);
		router.OnUrlReceived(null, BuildRequest("https://forwarded.example/a"));
		router.OnUrlReceived(null, BuildRequest("https://forwarded.example/b"));

		RecordingTarget target = new(onDelivery: index =>
		{
			if (index == 0)
			{
				throw new InvalidOperationException("simulated target failure");
			}
		});

		router.ActivateSession(target);
		router.OnUrlReceived(null, BuildRequest("https://forwarded.example/c"));

		_ = target.Received.Should().Equal("https://forwarded.example/a", "https://forwarded.example/b", "https://forwarded.example/c");
	}

	// A target that forwards back into the router from inside a delivery (same thread) must
	// not deadlock, and the re-entrant URL lands after everything already queued.
	[Fact]
	public void TargetReenteringTheRouterDuringDelivery_DoesNotDeadlock_AndKeepsArrivalOrder()
	{
		ForwardedUrlRouter router = new(NullLogger<ForwardedUrlRouter>.Instance);
		router.OnUrlReceived(null, BuildRequest("https://forwarded.example/a"));
		router.OnUrlReceived(null, BuildRequest("https://forwarded.example/b"));

		RecordingTarget target = new(onDelivery: index =>
		{
			if (index == 0)
			{
				router.OnUrlReceived(null, BuildRequest("https://forwarded.example/c"));
			}
		});

		Thread activator = new(() => router.ActivateSession(target)) { IsBackground = true };
		activator.Start();
		_ = activator.Join(TimeSpan.FromSeconds(10)).Should().BeTrue("a re-entrant forward must not deadlock the router");

		_ = target.Received.Should().Equal("https://forwarded.example/a", "https://forwarded.example/b", "https://forwarded.example/c");
	}

	// A session deactivated mid-delivery stops receiving; what was not yet delivered stays
	// buffered (never dropped) and reaches the next session in arrival order.
	[Fact]
	public void SessionDeactivatedDuringDelivery_KeepsUndeliveredUrlsBufferedForTheNextSession()
	{
		ForwardedUrlRouter router = new(NullLogger<ForwardedUrlRouter>.Instance);
		router.OnUrlReceived(null, BuildRequest("https://forwarded.example/a"));
		router.OnUrlReceived(null, BuildRequest("https://forwarded.example/b"));
		router.OnUrlReceived(null, BuildRequest("https://forwarded.example/c"));

		RecordingTarget? first = null;
		first = new RecordingTarget(onDelivery: index =>
		{
			if (index == 0)
			{
				router.DeactivateSession(first!);
			}
		});

		router.ActivateSession(first);
		_ = first.Received.Should().Equal("https://forwarded.example/a");

		RecordingTarget second = new(onDelivery: static _ => { });
		router.ActivateSession(second);
		_ = second.Received.Should().Equal("https://forwarded.example/b", "https://forwarded.example/c");
	}

	// Records every delivery AT ENTRY (so a blocked or throwing delivery still counts in
	// order), then runs the per-delivery hook with the delivery's zero-based index.
	private sealed class RecordingTarget(Action<int> onDelivery) : IPickerForwardTarget
	{
		private readonly Lock _gate = new();
		private readonly List<string> _received = [];

		public string[] Received
		{
			get
			{
				lock (_gate)
				{
					return [.. _received];
				}
			}
		}

		public void OnForwardedActivation(ActivationRequest request)
		{
			int index;
			lock (_gate)
			{
				index = _received.Count;
				_received.Add(request.NormalizedUrl);
			}

			onDelivery(index);
		}
	}

	private sealed class ApprovedTrustPolicyProvider : IIpcTrustPolicyProvider
	{
		public bool TryGetPolicy(out IpcTrustPolicy? policy, out IpcPeerAuthenticationFailure? failure)
		{
			policy = new IpcTrustPolicy(
				IpcTrustPolicyGate.RequiredSubjectId,
				IpcTrustPolicyGate.RequiredScope,
				"approved",
				"PASS",
				"x509-chain",
				false,
				"DH-2026-09-11",
				"owner@brochoos",
				DateTimeOffset.UtcNow);
			failure = null;
			return true;
		}
	}

	// Owns the primary side on a dedicated thread for the test's whole duration:
	// the mutex is acquired AND released on the same (owner) thread - the guard's
	// own Dispose only swallows a wrong-thread ReleaseMutex, which would otherwise
	// leave the Global mutex stuck-owned for the NEXT test in the suite.
	private static ActivationRequest BuildRequest(string url)
	{
		return new ActivationRequest(
			url,
			1,
			ActivationTriggerContext.CommandLine,
			SourceEvidence.Confirmed("test"),
			ActivationModifiers.None,
			Guid.NewGuid(),
			1,
			DateTimeOffset.UtcNow.AddMinutes(5));
	}

	private sealed class PrimaryGuardHost : IDisposable
	{
		private readonly ManualResetEventSlim _acquired = new();
		private readonly ManualResetEventSlim _stop = new();
		private readonly Thread _thread;

		internal PrimaryGuardHost(string url)
		{
			// Probe BEFORE the guard exists: skip only when the per-user mutex is demonstrably
			// held by another process. When it is free the first acquirer must be Primary, and
			// the tests assert exactly that instead of skipping.
			Skip.If(SharedGuardMutex.IsHeldElsewhere(), SharedGuardMutex.SkipReason);

			Guard = NewGuard();
			_thread = new Thread(Run) { IsBackground = true };
			_thread.Start();

			void Run()
			{
				Role = Guard.AcquireOrForwardAsync(BuildRequest(url), CancellationToken.None).GetAwaiter().GetResult();
				_acquired.Set();
				_stop.Wait();
				Guard.Dispose();
			}
		}

		internal WindowsSingleInstanceGuard Guard { get; }

		internal SingleInstanceRole Role { get; private set; } = SingleInstanceRole.Failed;

		internal bool WaitForRole(TimeSpan timeout)
		{
			return _acquired.Wait(timeout);
		}

		public void Dispose()
		{
			_stop.Set();
			_ = _thread.Join(TimeSpan.FromSeconds(10));
			_acquired.Dispose();
			_stop.Dispose();
		}
	}
}
