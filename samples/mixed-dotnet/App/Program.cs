using Daml.Runtime.Data;
using MiniDemo.Asset;

// Instrument is the template in daml\daml\MiniDemo\Asset.daml, as C# the build generated from its DAR.
var gold = new Instrument(new Party("Bank"), "Gold");
Console.WriteLine($"{gold.Name} from Daml package {Instrument.PackageName} {Instrument.PackageVersion}");
