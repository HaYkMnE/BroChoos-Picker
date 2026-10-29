using BroChoos.Core.Ingress;

namespace BroChoos.Core.Abstractions;

public enum SingleInstanceRole
{
	Primary,
	Secondary,
	Failed
}

public interface ISingleInstanceGuard
{
	Task<SingleInstanceRole> AcquireOrForwardAsync(ActivationRequest request, CancellationToken cancellationToken);
}
