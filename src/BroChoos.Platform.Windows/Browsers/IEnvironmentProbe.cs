namespace BroChoos.Platform.Windows.Browsers;

// Single seam over the ambient OS environment (variables, file existence, own
// process path). Detection and launching are pure logic on top of it, so tests
// can describe a whole machine state without touching the real disk.
public interface IEnvironmentProbe
{
	string? GetEnvironmentVariable(string name);

	bool FileExists(string path);

	// D-304 recursion guard input: the path of the currently running executable.
	string? CurrentProcessPath { get; }
}

public sealed class SystemEnvironmentProbe : IEnvironmentProbe
{
	public string? CurrentProcessPath => Environment.ProcessPath;

	public string? GetEnvironmentVariable(string name)
	{
		return Environment.GetEnvironmentVariable(name);
	}

	public bool FileExists(string path)
	{
		return File.Exists(path);
	}
}
