namespace BroChoos.Core.Abstractions;

// The three association types BroChoos declares link-handler capabilities for in
// 0A-4 (D-187, v13 §14). Declaring capability is NOT becoming the default: the
// user picks the default in Windows Settings, and BroChoos never writes
// UserChoice (hard invariant, D-187).
public enum DefaultAppsTarget
{
	Http,
	Https,
	UrlFile,
}
