using System.Reflection;
using System.Runtime.InteropServices;

namespace VisionCanvas.Tests;

public sealed class CanvasBoundaryTests
{
    [Fact]
    public void CanvasDependencyGraphDoesNotIncludeApplicationOrInspectionAssemblies()
    {
        var visited = new HashSet<string>();
        Visit(Assembly.Load("VisionCanvas"));
        Assert.DoesNotContain(visited, IsApplicationOrInspection);

        void Visit(Assembly assembly)
        {
            if (!visited.Add(assembly.GetName().Name!))
            {
                return;
            }
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (!IsRuntime(reference.Name!))
                {
                    Visit(Assembly.Load(reference));
                }
            }
        }
    }

    [Theory]
    [InlineData("VisionGeometry")]
    [InlineData("VisionImaging.Abstractions")]
    [InlineData("VisionCanvas.Core")]
    public void SharedContractsAndEditingCoreHaveOnlyNeutralManagedDependencies(string assemblyName)
    {
        var assembly = Assembly.Load(assemblyName);
        Assert.All(assembly.GetReferencedAssemblies(), reference =>
            Assert.True(IsRuntime(reference.Name!) || reference.Name is "VisionGeometry" or "VisionImaging.Abstractions",
                $"{assemblyName} depends on {reference.Name}."));
        Assert.All(assembly.GetTypes().SelectMany(type => type.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)),
            method => Assert.Null(method.GetCustomAttribute<DllImportAttribute>()));
    }

    [Fact]
    public void CanvasPublicApiDoesNotExposeApplicationOrInspectionTypes()
    {
        foreach (var type in Assembly.Load("VisionCanvas").GetExportedTypes())
        {
            AssertIndependent(type);
            if (type.BaseType is { } baseType)
            {
                AssertIndependent(baseType);
            }
            foreach (var contract in type.GetInterfaces())
            {
                AssertIndependent(contract);
            }
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                switch (member)
                {
                    case MethodBase method:
                        foreach (var parameter in method.GetParameters())
                        {
                            AssertIndependent(parameter.ParameterType);
                        }
                        if (method is MethodInfo function)
                        {
                            AssertIndependent(function.ReturnType);
                        }
                        break;
                    case PropertyInfo property:
                        AssertIndependent(property.PropertyType);
                        break;
                    case FieldInfo field:
                        AssertIndependent(field.FieldType);
                        break;
                    case EventInfo @event:
                        AssertIndependent(@event.EventHandlerType!);
                        break;
                }
            }
        }
    }

    private static void AssertIndependent(Type type)
    {
        Assert.False(IsApplicationOrInspection(type.Assembly.GetName().Name!), $"Public API exposes {type}.");
        Assert.False(type.Namespace?.StartsWith("TeachingToolWpf", StringComparison.Ordinal) == true,
            $"Public API retains the application namespace: {type}.");
        if (type.HasElementType)
        {
            AssertIndependent(type.GetElementType()!);
        }
        foreach (var argument in type.GetGenericArguments())
        {
            AssertIndependent(argument);
        }
    }

    private static bool IsApplicationOrInspection(string name)
        => name.StartsWith("TeachingToolWpf", StringComparison.Ordinal) ||
           name.StartsWith("VisionInspection", StringComparison.Ordinal) ||
           name.StartsWith("VisionAlgorithm", StringComparison.Ordinal) ||
           name.StartsWith("VisionCompute", StringComparison.Ordinal);

    private static bool IsRuntime(string name)
        => name.StartsWith("System", StringComparison.Ordinal) || name is "netstandard" or "mscorlib";
}
