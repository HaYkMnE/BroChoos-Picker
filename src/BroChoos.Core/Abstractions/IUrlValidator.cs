namespace BroChoos.Core.Abstractions;

public interface IUrlValidator
{
	bool IsAcceptable(string raw, out string normalized);
}
