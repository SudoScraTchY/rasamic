using Xunit;

// Integration tests bind shared sockets (beacon :47301, mDNS multicast) and
// pace real-time audio; parallel classes starve each other's timeouts.
[assembly: CollectionBehavior(DisableTestParallelization = true)]