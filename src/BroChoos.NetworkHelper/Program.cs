namespace BroChoos.NetworkHelper;

internal static class Program
{
	public static async Task<int> Main(string[] args)
	{
		return await ShortLinkProgram.RunAsync(args);
	}
}
