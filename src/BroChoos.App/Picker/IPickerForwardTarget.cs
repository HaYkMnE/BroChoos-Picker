using BroChoos.Core.Ingress;

namespace BroChoos.App.Picker;

// Receiver for activations forwarded by secondary instances over the existing
// single-instance pipe. Implemented by PickerSession; routed by ForwardedUrlRouter.
internal interface IPickerForwardTarget
{
	// Called on the guard's pipe-server thread; the implementation marshals all
	// state changes to the UI thread itself.
	void OnForwardedActivation(ActivationRequest request);
}
