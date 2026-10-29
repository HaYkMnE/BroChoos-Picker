using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BroChoos.Core.Abstractions;
using BroChoos.NetworkHelper;
using FluentAssertions;
using Xunit;

namespace BroChoos.Security.Tests;

public sealed class ShortLinkPacketTranscriptEvidenceTests
{
	[Fact]
	public async Task CaptureTranscript_HeadManualRedirect_NoSensitiveHeadersLeak()
	{
		await using LoopbackTranscriptServer server = await LoopbackTranscriptServer.StartAsync(
			new ScriptedResponse(
				"HTTP/1.1 302 Found",
				new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					["Location"] = "http://example.org/final",
					["Content-Length"] = "0",
					["Connection"] = "close",
				}),
			new ScriptedResponse(
				"HTTP/1.1 200 OK",
				new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					["Content-Length"] = "0",
					["Connection"] = "close",
				}));

		ShortLinkResolveResponse response = await ResolveViaProxyAsync(server.ProxyUri, "http://bit.ly/start");

		_ = response.Dispatch.Should().Be(HelperClientDispatch.Dispatched);
		_ = response.ErrorCode.Should().BeNull();
		_ = response.EffectiveUrl.Should().Be("http://example.org/final");

		IReadOnlyList<CapturedExchange> exchanges = await server.StopAndGetExchangesAsync();
		AssertNoSensitiveHeadersAndHeadMethod(exchanges);
		_ = exchanges.Should().HaveCount(2, "resolver must do manual HEAD redirect resolution over two requests");
		_ = exchanges[0].ResponseStatusLine.Should().Be("HTTP/1.1 302 Found");
		_ = exchanges[0].ResponseHeaders["Location"].Should().Be("http://example.org/final");
		_ = exchanges[1].ResponseStatusLine.Should().Be("HTTP/1.1 200 OK");

		await WriteAndVerifyTranscriptAsync("w8.3-shortlink-loopback-transcript.txt", BuildTranscript(exchanges, response));
	}

	[Fact]
	public async Task CaptureTranscript_GuardRejectsPrivateTarget_AfterManualRedirectStep()
	{
		await using LoopbackTranscriptServer server = await LoopbackTranscriptServer.StartAsync(
			new ScriptedResponse(
				"HTTP/1.1 302 Found",
				new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					["Location"] = "http://127.0.0.1/internal",
					["Content-Length"] = "0",
					["Connection"] = "close",
				}));

		ShortLinkResolveResponse response = await ResolveViaProxyAsync(server.ProxyUri, "http://bit.ly/private-guard");

		_ = response.Dispatch.Should().Be(HelperClientDispatch.Rejected);
		_ = response.ErrorCode.Should().Be("shortlink.private-target");

		IReadOnlyList<CapturedExchange> exchanges = await server.StopAndGetExchangesAsync();
		AssertNoSensitiveHeadersAndHeadMethod(exchanges);
		_ = exchanges.Should().HaveCount(1, "private-target guard must stop before issuing a second outbound request");
		_ = exchanges[0].ResponseStatusLine.Should().Be("HTTP/1.1 302 Found");
		_ = exchanges[0].ResponseHeaders["Location"].Should().Be("http://127.0.0.1/internal");

		await WriteAndVerifyTranscriptAsync("w8.3-shortlink-loopback-private-guard-transcript.txt", BuildTranscript(exchanges, response));
	}

	// Opt-in switch for writing the transcript evidence under artifacts/evidence/network/ (ignored by git).
	// Unset (the default for every `dotnet test`, CI included) the transcript goes to a per-run
	// temp folder, so a routine test run never leaves files in the repository. Set to exactly
	// "1" to regenerate the evidence on purpose through this same code path.
	internal const string WriteEvidenceVariable = "BROCHOOS_WRITE_EVIDENCE";

	// Writes the transcript, reads it back and asserts the bytes landed intact. The target is the
	// tracked evidence folder ONLY when WriteEvidenceVariable=1; otherwise a fresh per-run folder
	// under %TEMP% that is removed afterwards.
	private static async Task WriteAndVerifyTranscriptAsync(string fileName, string transcript)
	{
		bool regenerateEvidence = string.Equals(
			Environment.GetEnvironmentVariable(WriteEvidenceVariable), "1", StringComparison.Ordinal);

		string directory = regenerateEvidence
			? GetEvidenceDirectory()
			: Path.Combine(Path.GetTempPath(), "brochoos-w8.3-transcripts", Guid.NewGuid().ToString("N"));

		try
		{
			_ = Directory.CreateDirectory(directory);
			string artifactPath = Path.Combine(directory, fileName);
			await File.WriteAllTextAsync(artifactPath, transcript, Encoding.UTF8);

			_ = File.Exists(artifactPath).Should().BeTrue("the transcript artifact must be written");
			_ = (await File.ReadAllTextAsync(artifactPath, Encoding.UTF8)).Should().Be(
				transcript, "the written transcript must round-trip byte-for-byte");
		}
		finally
		{
			if (!regenerateEvidence && Directory.Exists(directory))
			{
				Directory.Delete(directory, recursive: true);
			}
		}
	}

	private static async Task<ShortLinkResolveResponse> ResolveViaProxyAsync(Uri proxyUri, string requestUrl)
	{
		using HttpClientHandler handler = new()
		{
			AllowAutoRedirect = false,
			UseCookies = false,
			UseProxy = true,
			Proxy = new WebProxy(proxyUri),
			AutomaticDecompression = DecompressionMethods.None,
		};

		ShortLinkResolver resolver = new(
			new HttpMessageInvoker(handler),
			(_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

		return await resolver.ResolveAsync(
			new ShortLinkResolveRequest(requestUrl, ConsentGranted: true),
			CancellationToken.None);
	}

	private static void AssertNoSensitiveHeadersAndHeadMethod(IReadOnlyList<CapturedExchange> exchanges)
	{
		_ = exchanges.Should().NotBeEmpty();
		_ = exchanges.All(x => x.Method == "HEAD").Should().BeTrue();
		_ = exchanges.All(x => !x.Headers.ContainsKey("Authorization")).Should().BeTrue();
		_ = exchanges.All(x => !x.Headers.ContainsKey("Cookie")).Should().BeTrue();
		_ = exchanges.All(x => !x.Headers.ContainsKey("Referer")).Should().BeTrue();
		_ = exchanges.All(x => !x.Headers.ContainsKey("Proxy-Authorization")).Should().BeTrue();
	}

	private static string BuildTranscript(IReadOnlyList<CapturedExchange> exchanges, ShortLinkResolveResponse response)
	{
		StringBuilder sb = new();
		_ = sb.AppendLine("W8.3 loopback application-layer packet transcript (unprivileged)");
		_ = sb.AppendLine("Source: ShortLinkResolver runtime path with HttpClientHandler + proxy + manual HEAD redirects");
		_ = sb.AppendLine(string.Concat(
			"Result: dispatch=",
			response.Dispatch.ToString(),
			", effectiveUrl=",
			response.EffectiveUrl,
			", errorCode=",
			response.ErrorCode ?? "<none>"));
		_ = sb.AppendLine("Invariant checks: method=HEAD, redirect handled manually, no Authorization/Cookie/Referer/Proxy-Authorization");
		_ = sb.AppendLine();

		for (int i = 0; i < exchanges.Count; i++)
		{
			CapturedExchange x = exchanges[i];
			_ = sb.AppendLine(string.Concat("=== Exchange ", (i + 1).ToString(CultureInfo.InvariantCulture), " ==="));
			_ = sb.AppendLine("Request:");
			_ = sb.AppendLine(x.RawRequest);
			_ = sb.AppendLine("Response:");
			_ = sb.AppendLine(x.RawResponse);
			_ = sb.AppendLine();
		}

		return sb.ToString();
	}

	private static string GetEvidenceDirectory()
	{
		DirectoryInfo? dir = new(AppContext.BaseDirectory);
		while (dir is not null)
		{
			if (File.Exists(Path.Combine(dir.FullName, "BroChoos.sln")))
			{
				return Path.Combine(dir.FullName, "artifacts", "evidence", "network");
			}

			dir = dir.Parent;
		}

		throw new InvalidOperationException("Repository root with BroChoos.sln was not found.");
	}

	private sealed record ScriptedResponse(string StatusLine, IReadOnlyDictionary<string, string> Headers);

	private sealed record CapturedExchange(
		string Method,
		string RequestTarget,
		IReadOnlyDictionary<string, string> Headers,
		string RawRequest,
		string ResponseStatusLine,
		IReadOnlyDictionary<string, string> ResponseHeaders,
		string RawResponse);

	private sealed class LoopbackTranscriptServer : IAsyncDisposable
	{
		private readonly TcpListener _listener;
		private readonly CancellationTokenSource _serverCts;
		private readonly Task _serverTask;
		private readonly List<CapturedExchange> _exchanges;
		private readonly int _port;

		private LoopbackTranscriptServer(TcpListener listener, CancellationTokenSource serverCts, Task serverTask, List<CapturedExchange> exchanges, int port)
		{
			_listener = listener;
			_serverCts = serverCts;
			_serverTask = serverTask;
			_exchanges = exchanges;
			_port = port;
		}

		public Uri ProxyUri => new($"http://127.0.0.1:{_port}");

		public static Task<LoopbackTranscriptServer> StartAsync(params ScriptedResponse[] responses)
		{
			if (responses.Length == 0)
			{
				throw new ArgumentException("At least one scripted response is required.", nameof(responses));
			}

			TcpListener listener = new(IPAddress.Loopback, 0);
			listener.Start();
			int port = ((IPEndPoint)listener.LocalEndpoint).Port;

			CancellationTokenSource cts = new();
			List<CapturedExchange> exchanges = [];
			Task serverTask = RunServerAsync(listener, exchanges, responses, cts.Token);

			return Task.FromResult(new LoopbackTranscriptServer(listener, cts, serverTask, exchanges, port));
		}

		public async Task<IReadOnlyList<CapturedExchange>> StopAndGetExchangesAsync()
		{
			_serverCts.Cancel();
			_listener.Stop();
			try
			{
				await _serverTask;
			}
			catch (OperationCanceledException) when (_serverCts.IsCancellationRequested)
			{
			}
			catch (SocketException) when (_serverCts.IsCancellationRequested)
			{
			}

			return [.. _exchanges];
		}

		public async ValueTask DisposeAsync()
		{
			_ = await StopAndGetExchangesAsync();
			_serverCts.Dispose();
		}

		private static async Task RunServerAsync(TcpListener listener, List<CapturedExchange> exchanges, IReadOnlyList<ScriptedResponse> responses, CancellationToken cancellationToken)
		{
			while (!cancellationToken.IsCancellationRequested && exchanges.Count < responses.Count)
			{
				using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
				using NetworkStream stream = client.GetStream();
				CapturedRequest req = await ReadRequestAsync(stream, cancellationToken);

				ScriptedResponse scripted = responses[exchanges.Count];
				string rawResponse = BuildRawResponse(scripted.StatusLine, scripted.Headers);
				byte[] responseBytes = Encoding.ASCII.GetBytes(rawResponse);
				await stream.WriteAsync(responseBytes, cancellationToken);
				await stream.FlushAsync(cancellationToken);

				exchanges.Add(new CapturedExchange(
					req.Method,
					req.Target,
					req.Headers,
					req.RawRequest,
					scripted.StatusLine,
					scripted.Headers,
					rawResponse));
			}
		}

		private sealed record CapturedRequest(string Method, string Target, Dictionary<string, string> Headers, string RawRequest);

		private static async Task<CapturedRequest> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
		{
			using StreamReader reader = new(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

			string? requestLine = await reader.ReadLineAsync(cancellationToken);
			if (string.IsNullOrWhiteSpace(requestLine))
			{
				throw new InvalidOperationException("Loopback server received empty request line.");
			}

			string[] lineParts = requestLine.Split(' ');
			if (lineParts.Length < 2)
			{
				throw new InvalidOperationException($"Malformed request line: {requestLine}");
			}

			Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
			List<string> headerLines = [];
			while (true)
			{
				string? line = await reader.ReadLineAsync(cancellationToken);
				if (line is null)
				{
					break;
				}

				if (line.Length == 0)
				{
					break;
				}

				headerLines.Add(line);
				int colon = line.IndexOf(':');
				if (colon > 0)
				{
					string name = line[..colon].Trim();
					string value = line[(colon + 1)..].Trim();
					headers[name] = value;
				}
			}

			StringBuilder raw = new();
			_ = raw.AppendLine(requestLine);
			foreach (string header in headerLines)
			{
				_ = raw.AppendLine(header);
			}
			_ = raw.AppendLine();

			return new CapturedRequest(lineParts[0], lineParts[1], headers, raw.ToString());
		}

		private static string BuildRawResponse(string statusLine, IReadOnlyDictionary<string, string> headers)
		{
			StringBuilder sb = new();
			_ = sb.AppendLine(statusLine);
			foreach ((string name, string value) in headers)
			{
				_ = sb.AppendLine(string.Concat(name, ": ", value));
			}
			_ = sb.AppendLine();
			return sb.ToString();
		}
	}
}
