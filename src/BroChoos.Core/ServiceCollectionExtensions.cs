using BroChoos.Core.Cli;
using BroChoos.Core.Runners;
using Microsoft.Extensions.DependencyInjection;

namespace BroChoos.Core;

public static class ServiceCollectionExtensions
{
	public static IServiceCollection AddCoreServices(this IServiceCollection services)
	{
		_ = services.AddSingleton<Bootstrap>();
		_ = services.AddSingleton<CliParser>();
		_ = services.AddSingleton<UrlRunner>();
		_ = services.AddSingleton<SafeModeRunner>();
		_ = services.AddSingleton<DefaultsRunner>();

		return services;
	}
}
