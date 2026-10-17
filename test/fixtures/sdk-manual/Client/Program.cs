using Daml.Runtime.Data;
using SdkManual.Asset;

// Asset comes from Contracts\daml\Asset.daml, generated into obj by the build.
var asset = new Asset(new Party("Alice"), new Party("Bob"), "Gold");
System.Console.WriteLine($"{asset.Name} from package {Asset.PackageName} {Asset.PackageVersion} ({Asset.PackageId})");
