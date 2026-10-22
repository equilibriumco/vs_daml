using Daml.Runtime.Data;
using DamlApp.Contracts.Main;

// Asset is the template in DamlApp.Contracts\daml\Main.daml, as C# the build generated from its DAR.
var asset = new Asset(new Party("Alice"), new Party("Bob"), "Gold");
Console.WriteLine($"{asset.Name} from Daml package {Asset.PackageName} {Asset.PackageVersion}");
