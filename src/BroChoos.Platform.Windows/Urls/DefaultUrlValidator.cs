using BroChoos.Core.Abstractions;

namespace BroChoos.Platform.Windows.Urls;

public sealed class DefaultUrlValidator : IUrlValidator
{
	public bool IsAcceptable(string raw, out string normalized)
	{
		normalized = string.Empty;

		if (string.IsNullOrWhiteSpace(raw))
		{
			return false;
		}

		if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri))
		{
			return false;
		}

		if (uri.Scheme is not ("http" or "https"))
		{
			return false;
		}

		normalized = uri.AbsoluteUri;
		return true;
	}
}
