// Prints the shared-contract API id (NetPI.Abstractions.ContractId) of the
// netPI.Abstractions.dll at the given path. Used by tools/publish-plugins.ps1
// to pin every published artifact against the HOST's current contract.
//
// usage: netpi-apiid <path-to-netPI.Abstractions.dll>
Console.WriteLine(NetPI.Abstractions.ContractId.ComputeFromFile(args[0]));
return 0;
