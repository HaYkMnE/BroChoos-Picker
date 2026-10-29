using System.Diagnostics;

namespace BroChoos.Platform.Windows.Browsers;

// D-183: the only process-start seam in the product. It receives a fully built
// ProcessStartInfo (structured ArgumentList, UseShellExecute=false) and never a
// command string, so no implementation can introduce a shell.
public interface IProcessStarter
{
	// Returns true when the OS handed back a process object for the request.
	bool Start(ProcessStartInfo startInfo);
}

public sealed class ProcessStarter : IProcessStarter
{
	public bool Start(ProcessStartInfo startInfo)
	{
		// The child browser outlives BroChoos; we only need to know it started.
		using Process? process = Process.Start(startInfo);
		return process is not null;
	}
}
