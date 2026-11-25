using System.Reflection;

using ProcBridge.Relay.Services;

namespace ProcBridge.Relay.Tests.Unit;

[TestClass]
public sealed class ServiceRegistrationTests
{
    [TestMethod]
    public void MọiAssemblyServicesĐềuCóMốcTrongFacade()
    {
        var expected = Directory.GetFiles(AppContext.BaseDirectory, "ProcBridge.Relay.Services.*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Order()
            .ToArray();
        var actual = AppServiceCollectionExtensions.FeatureAssemblies.Select(x => x.GetName().Name).Order().ToArray();
        CollectionAssert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void FacadeGọiMọiHàmAddFeatureServices()
    {
        var addMethods = AppServiceCollectionExtensions.FeatureAssemblies
            .SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.Name.StartsWith("Add", StringComparison.Ordinal) && m.Name.EndsWith("Services", StringComparison.Ordinal))
            .Select(m => m.Name)
            .Order()
            .ToArray();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddApplicationServices();
        // Each feature registers at least one service from its own assembly.
        foreach (var assembly in AppServiceCollectionExtensions.FeatureAssemblies)
            Assert.IsTrue(services.Any(d => d.ImplementationType?.Assembly == assembly || d.ServiceType.Assembly == assembly),
                $"{assembly.GetName().Name} chưa được facade đăng ký.");
        Assert.HasCount(AppServiceCollectionExtensions.FeatureAssemblies.Length, addMethods);
    }
}
