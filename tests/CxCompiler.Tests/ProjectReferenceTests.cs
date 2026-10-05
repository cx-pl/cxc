using CxCompiler.Model.Common;
using CxCompiler.Model.Project;
using CxCompiler.Semantics;
using System.Text.RegularExpressions;

namespace CxCompiler.Tests;

public sealed class ProjectReferenceTests
{
    [Fact]
    [Trait("Area", "CrossModule")]
    public void ProjectReferenceBindsPublicSymbolsToTheirOwningModuleAndGeneratesSeparateUnits()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cxc-project-refs-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "math");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(library, "math.cxproj"), "name: math\ntype: Library\n");
            File.WriteAllText(Path.Combine(library, "api.cx"), "namespace Math; public struct Value { public int number; } public interface IValue { int Get(); } public int AddOne(int value) { return value + 1; }");
            File.WriteAllText(Path.Combine(app, "app.cxproj"), "name: app\ntype: Executable\nproject_references:\n- ../math/math.cxproj\n");
            File.WriteAllText(Path.Combine(app, "main.cx"), "import Math; namespace App; public class Implementation : IValue { public constructor() {} public int Get() { return 41; } } public int Run() { Implementation value = new Implementation(); IValue reference = value; Value number; number.number = 41; return AddOne(reference.Get() + number.number - 41); }");

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            var appSource = File.ReadAllText(Path.Combine(app, ".obj", "app.c"));
            var appHeader = File.ReadAllText(Path.Combine(app, ".obj", "app.internal.h"));
            var libraryHeader = File.ReadAllText(Path.Combine(library, ".obj", "math.h"));
            var cmake = File.ReadAllText(Path.Combine(app, ".obj", "CMakeLists.txt"));
            var librarySource = File.ReadAllText(Path.Combine(library, ".obj", "math.c"));
            Assert.Contains("CX_ID_3(math, Math, AddOne)(", appSource);
            Assert.Contains("CX_ID_3(math, Math, Value)", appSource);
            Assert.Contains("cx_iface_ref", appSource);
            Assert.Contains("#include \"math.h\"", appHeader);
            Assert.Contains("target_link_libraries(app PRIVATE math)", cmake);
            Assert.Contains("copy_if_different \"$<TARGET_FILE:math>\"", cmake);
            Assert.Contains("CX_ID_3(math, Math, AddOne)", librarySource);
            Assert.DoesNotContain("CX_ID_3(math, Math, AddOne)", appSource.Split("Run", 2)[0]);
            Assert.Contains("option(CX_STATIC_LINK", cmake);
            Assert.Contains("__cx_module_init_APP();", appSource);
            Assert.Contains("__cx_module_init_MATH();", appSource);
            Assert.Contains("(struct cx_interface_impl)", appSource);
            Assert.Contains("CX_MATH_DATA_API CX_IMPORT", libraryHeader);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectReferenceCyclesAreReportedBeforeCodeGeneration()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cxc-project-cycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "a.cxproj"), "name: a\nproject_references:\n- b.cxproj\n");
            File.WriteAllText(Path.Combine(root, "b.cxproj"), "name: b\nproject_references:\n- a.cxproj\n");
            var error = Assert.Throws<CxCompiler.Model.Errors.CompilationErrorException>(
                () => new Compiler().Compile([Path.Combine(root, "a.cxproj")]));
            Assert.Contains("Project reference cycle", error.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(root, ".obj")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectReferenceCanSpecializeAnImportedGenericFunction()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cxc-project-generic-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "generic");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(library, "generic.cxproj"), "name: generic\ntype: Library\n");
            File.WriteAllText(Path.Combine(library, "api.cx"), "namespace Lib; public T Identity<T>(T value) { return value; }");
            File.WriteAllText(Path.Combine(app, "app.cxproj"), "name: app\ntype: Executable\nproject_references:\n- ../generic/generic.cxproj\n");
            File.WriteAllText(Path.Combine(app, "main.cx"), "import Lib; namespace App; public int Run() { return Identity(42); }");

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            var appSource = File.ReadAllText(Path.Combine(app, ".obj", "app.c"));
            Assert.Contains("return value;", appSource);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectReferenceCanCloseAnImportedGenericClass()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cxc-project-generic-type-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "generic");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(library, "generic.cxproj"), "name: generic\ntype: Library\n");
            File.WriteAllText(Path.Combine(library, "api.cx"),
                "namespace Lib; public class Box<T> { public T Value; public constructor() {} } public class Crate<T> { public T Value; public constructor() {} } public int CreateBox() { Box<int> box = new Box<int>(); box.Value = 41; return box.Value; }");
            File.WriteAllText(Path.Combine(app, "app.cxproj"),
                "name: app\ntype: Executable\nproject_references:\n- ../generic/generic.cxproj\n");
            File.WriteAllText(Path.Combine(app, "main.cx"),
                "import Lib; namespace App; public int Run() { Box<int> box = new Box<int>(); Crate<int> crate = new Crate<int>(); box.Value = 42; crate.Value = box.Value; return crate.Value + CreateBox(); }");

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            var appSource = File.ReadAllText(Path.Combine(app, ".obj", "app.c"));
            var appHeader = File.ReadAllText(Path.Combine(app, ".obj", "app.internal.h"));
            var genericHeader = File.ReadAllText(Path.Combine(library, ".obj", "generic.h"));
            var genericSource = File.ReadAllText(Path.Combine(library, ".obj", "generic.c"));
            var dependencyBoxIdentity = Regex.Match(genericHeader,
                @"struct (cx_generic_[0-9a-f]+) \{").Groups[1].Value;
            Assert.NotEmpty(dependencyBoxIdentity);
            Assert.Contains("cx_generic_", appHeader);
            Assert.DoesNotContain($"CX_BEGIN_VTABLE_DEF({dependencyBoxIdentity})", appSource);
            Assert.Contains("CX_BEGIN_VTABLE_DEF(cx_generic_", appSource);
            Assert.Contains("CX_BEGIN_VTABLE_DEF(cx_generic_", genericSource);
            Assert.Contains("CX_STRING_DEF(CX_ID_2(cx_generic_", appSource);
            Assert.Contains("cx_generic_", appSource);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectReferenceDoesNotExposePrivateFunctions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cxc-project-private-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "library");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(library, "library.cxproj"), "name: library\ntype: Library\n");
            File.WriteAllText(Path.Combine(library, "api.cx"), "namespace Lib; struct Internal {} int Hidden(Internal value) { return 1; }");
            File.WriteAllText(Path.Combine(app, "app.cxproj"), "name: app\nproject_references:\n- ../library/library.cxproj\n");
            File.WriteAllText(Path.Combine(app, "main.cx"), "import Lib; namespace App; public int Run() { return Hidden(); }");

            var error = Assert.Throws<CxCompiler.Model.Errors.CompilationErrorException>(
                () => new Compiler().Compile([Path.Combine(app, "app.cxproj")]));
            Assert.Contains("Cannot resolve function 'Hidden'", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsTypeNameCollisionsAcrossProjectBoundaries()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cxc-project-collision-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "library");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(library, "library.cxproj"), "name: library\ntype: Library\n");
            File.WriteAllText(Path.Combine(library, "api.cx"), "namespace Shared; public class Token {}");
            File.WriteAllText(Path.Combine(app, "app.cxproj"), "name: app\nproject_references:\n- ../library/library.cxproj\n");
            File.WriteAllText(Path.Combine(app, "main.cx"), "namespace Shared; public class Token {}");

            var error = Assert.Throws<CxCompiler.Model.Errors.CompilationErrorException>(
                () => new Compiler().Compile([Path.Combine(app, "app.cxproj")]));
            Assert.Contains("declared by multiple CX projects", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectReferenceCMakeOrderIsDeterministic()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cxc-project-order-{Guid.NewGuid():N}");
        var app = Path.Combine(root, "app");
        var alpha = Path.Combine(root, "alpha");
        var zeta = Path.Combine(root, "zeta");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(alpha);
        Directory.CreateDirectory(zeta);
        try
        {
            File.WriteAllText(Path.Combine(alpha, "alpha.cxproj"), "name: alpha\ntype: Library\n");
            File.WriteAllText(Path.Combine(zeta, "zeta.cxproj"), "name: zeta\ntype: Library\n");
            File.WriteAllText(Path.Combine(app, "app.cxproj"), "name: app\ntype: Executable\nproject_references:\n- ../zeta/zeta.cxproj\n- ../alpha/alpha.cxproj\n");

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            var cmake = File.ReadAllText(Path.Combine(app, ".obj", "CMakeLists.txt"));
            Assert.True(cmake.IndexOf("../alpha/.obj", StringComparison.Ordinal) <
                cmake.IndexOf("../zeta/.obj", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
