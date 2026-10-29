namespace BroChoos.Core.Storage;

// What the executor observed when it tried to OPEN the file. Deliberately NOT
// named FileAccess: implicit usings bring the framework's filesystem namespace
// into scope, and a same-named Core type would collide with it confusingly.
public enum ConfigFileAccess
{
	// The file is not there. Nothing to protect, nothing to preserve.
	Absent,

	// The file exists but could not be opened (locked by another process, denied
	// ACL). Owner decision 1: this ENDS the read path — defaults only, NO repair,
	// NO write of any kind. Distinct from Absent, because a file we cannot read
	// may well have been written by a newer version (D-066).
	OpenFailed,

	// Bytes were obtained. The envelope/body observations below are meaningful.
	Opened
}
