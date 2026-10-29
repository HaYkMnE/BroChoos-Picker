using System.Text.Json;
using BroChoos.Core.Abstractions;
using BroChoos.Platform.Windows.Browsers;
using BroChoos.Platform.Windows.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace BroChoos.StorageHelper;

internal static class Program
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	public static int Main(string[] args)
	{
		if (args.Length != 1)
		{
			return 2;
		}

		// The command is validated BEFORE stdin is touched: an unknown command must fail fast with
		// exit code 2 instead of blocking on (or consuming) a payload it will never use.
		bool discover = string.Equals(args[0], "discover-persist", StringComparison.Ordinal);
		bool publish = string.Equals(args[0], "publish-candidates", StringComparison.Ordinal);
		if (!discover && !publish)
		{
			return 2;
		}

		string payload = Console.In.ReadToEnd();

		return discover ? RunDiscovery(payload) : RunPublication(payload);
	}

	// W6.2: persist candidates the caller ALREADY validated. This deliberately never calls
	// IBrowserDetector: a manual/portable add must not turn into the machine scan the user
	// declined at first run, and a helper that detected here would do exactly that while
	// every test of the detection path stayed green.
	private static int RunPublication(string payload)
	{
		StorageCandidatePublicationRequest request;
		try
		{
			request = JsonSerializer.Deserialize<StorageCandidatePublicationRequest>(payload, JsonOptions)
				?? new StorageCandidatePublicationRequest([], null);
		}
		catch (JsonException)
		{
			WriteResponse(new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.helper-invalid-request"));
			return 3;
		}

		try
		{
			using ServiceProvider provider = BuildProvider();
			PostConsentDiscoveryPublisher publisher = provider.GetRequiredService<PostConsentDiscoveryPublisher>();
			StorageDiscoveryResponse response = publisher.Publish(request);
			WriteResponse(response);
			return response.Dispatch == HelperClientDispatch.Dispatched ? 0 : 4;
		}
		catch
		{
			WriteResponse(new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.helper-execution-failed"));
			return 5;
		}
	}

	private static int RunDiscovery(string payload)
	{
		StorageDiscoveryRequest request;
		try
		{
			request = JsonSerializer.Deserialize<StorageDiscoveryRequest>(payload, JsonOptions)
				?? new StorageDiscoveryRequest(null);
		}
		catch (JsonException)
		{
			WriteResponse(new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.helper-invalid-request"));
			return 3;
		}

		try
		{
			using ServiceProvider provider = BuildProvider();
			PostConsentDiscoveryPublisher publisher = provider.GetRequiredService<PostConsentDiscoveryPublisher>();
			StorageDiscoveryResponse response = publisher.Execute(request);
			WriteResponse(response);
			return response.Dispatch == HelperClientDispatch.Dispatched ? 0 : 4;
		}
		catch
		{
			WriteResponse(new StorageDiscoveryResponse(HelperClientDispatch.Rejected, null, "storage.helper-execution-failed"));
			return 5;
		}
	}

	private static ServiceProvider BuildProvider()
	{
		ServiceCollection services = new();
		_ = services.AddSingleton<IEnvironmentProbe, SystemEnvironmentProbe>();
		_ = services.AddSingleton<IRegistryBrowserSource, RegistryBrowserSource>();
		_ = services.AddSingleton<IBrowserDetector, WindowsBrowserDetector>();
		_ = services.AddSingleton(TimeProvider.System);
		_ = services.AddSingleton<IStorageFileSystem, SystemStorageFileSystem>();
		_ = services.AddSingleton<IStorageReadFileSystem, SystemStorageReadFileSystem>();
		_ = services.AddSingleton<ConfigFileObserver>();
		_ = services.AddSingleton<IConfigDocumentWriterFactory, AtomicConfigDocumentWriterFactory>();
		_ = services.AddSingleton<IBrowserCatalogPersistenceFactory, BrowserCatalogPersistenceFactory>();
		_ = services.AddSingleton<IPostConsentDiscoveryStore, PostConsentDiscoveryStore>();
		_ = services.AddSingleton<PostConsentDiscoveryPublisher>();
		_ = services.AddLogging();
		return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
	}

	private static void WriteResponse(StorageDiscoveryResponse response)
	{
		Console.Out.Write(JsonSerializer.Serialize(response, JsonOptions));
	}
}
