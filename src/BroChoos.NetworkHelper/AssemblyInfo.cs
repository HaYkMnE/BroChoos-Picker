using System.Runtime.CompilerServices;

// The unpinned HttpMessageInvoker test seam on ShortLinkResolver is internal on purpose: an
// injected invoker bypasses the connection pin, so production must not reach it. Making it
// internal turns "someone forgot to pin" into a compile error, not a silent runtime flag.
// The Security tests still need it to assert PinsConnectionsToValidatedAddress == false.
[assembly: InternalsVisibleTo("BroChoos.Security.Tests")]
