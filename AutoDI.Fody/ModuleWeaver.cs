﻿using AutoDI;
using AutoDI.Fody;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Linq;
using DependencyAttribute = AutoDI.DependencyAttribute;
using OpCodes = Mono.Cecil.Cil.OpCodes;

[assembly: InternalsVisibleTo("AutoDI.Fody.Tests")]
// ReSharper disable once CheckNamespace
public partial class ModuleWeaver
{
    // Will contain the full element XML from FodyWeavers.xml. OPTIONAL
    public XElement Config { get; set; }

    // Will log an MessageImportance.Normal message to MSBuild. OPTIONAL
    public Action<string> LogDebug { get; set; } = s => { };

    // Will log an MessageImportance.High message to MSBuild. OPTIONAL
    public Action<string> LogInfo { get; set; } = s => { };

    // Will log an warning message to MSBuild. OPTIONAL
    public Action<string> LogWarning { get; set; } = s => { };

    // Will log an warning message to MSBuild at a specific point in the code. OPTIONAL
    public Action<string, SequencePoint> LogWarningPoint { get; set; } = (s, p) => { };

    // Will log an error message to MSBuild. OPTIONAL
    public Action<string> LogError { get; set; } = s => { };

    // Will log an error message to MSBuild at a specific point in the code. OPTIONAL
    public Action<string, SequencePoint> LogErrorPoint { get; set; } = (s, p) => { };

    // An instance of Mono.Cecil.IAssemblyResolver for resolving assembly references. OPTIONAL
    public IAssemblyResolver AssemblyResolver { get; set; }

    // An instance of Mono.Cecil.ModuleDefinition for processing. REQUIRED
    public ModuleDefinition ModuleDefinition { get; set; }

    // Will contain the full path of the target assembly. OPTIONAL
    public string AssemblyFilePath { get; set; }

    // Will contain the full directory path of the target project. 
    // A copy of $(ProjectDir). OPTIONAL
    public string ProjectDirectoryPath { get; set; }

    // Will contain the full directory path of the current weaver. OPTIONAL
    public string AddinDirectoryPath { get; set; }

    // Will contain the full directory path of the current solution.
    // A copy of `$(SolutionDir)` or, if it does not exist, a copy of `$(MSBuildProjectDirectory)..\..\..\`. OPTIONAL
    public string SolutionDirectoryPath { get; set; }

    // Will contain a semicomma delimetered string that contains 
    // all the references for the target project. 
    // A copy of the contents of the @(ReferencePath). OPTIONAL
    public string References { get; set; }

    // Will a list of all the references marked as copy-local. 
    // A copy of the contents of the @(ReferenceCopyLocalPaths). OPTIONAL
    public List<string> ReferenceCopyLocalPaths { get; set; }

    // Will a list of all the msbuild constants. 
    // A copy of the contents of the $(DefineConstants). OPTIONAL
    public List<string> DefineConstants { get; set; }

    public string AssemblyToProcess { get; set; }

    private Action<string, DebugLogLevel> InternalLogDebug { get; set; } = (s, l) => { };

    public void Execute()
    {
        try
        {
            Settings settings = Settings.Parse(Config);
            InternalLogDebug = (s, l) =>
            {
                if (l <= settings.DebugLogLevel)
                {
                    LogDebug(s);
                }

            };

            ICollection<TypeDefinition> allTypes = GetAllTypes(settings);
            Mapping mapping = GetMapping(settings, allTypes);

            InternalLogDebug($"Found potential map:\r\n{mapping}", DebugLogLevel.Verbose);
            TypeDefinition resolverType = CreateAutoDIContainer(mapping);

            ModuleDefinition.Types.Add(resolverType);

            if (settings.InjectContainer)
            {
                InjectContainer(resolverType);
            }

            foreach (TypeDefinition type in allTypes)
            {
                ProcessType(type);
            }
        }
        catch (Exception ex)
        {
            var sb = new StringBuilder();
            for (Exception e = ex; e != null; e = e.InnerException)
                sb.AppendLine(e.ToString());
            LogError(sb.ToString());
        }
    }

    private ICollection<TypeDefinition> GetAllTypes(Settings settings)
    {
        var allTypes = new HashSet<TypeDefinition>(TypeComparer.FullName);
        IEnumerable<TypeDefinition> FilterTypes(IEnumerable<TypeDefinition> types) =>
            types.Where(t => !t.IsCompilerGenerated() && !allTypes.Remove(t));

        foreach (TypeDefinition type in FilterTypes(ModuleDefinition.GetAllTypes()))
        {
            allTypes.Add(type);
        }

        foreach (AssemblyNameReference assemblyReference in ModuleDefinition.AssemblyReferences)
        {
            bool useAutoDiAssebmlies = settings.Behavior.HasFlag(Behaviors.IncludeDependentAutoDIAssemblies);
            bool matchesAssembly = settings.Assemblies.Any(a => a.Matches(assemblyReference.FullName));
            if (useAutoDiAssebmlies || matchesAssembly)
            {
                AssemblyDefinition assembly = AssemblyResolver.Resolve(assemblyReference);
                if (assembly == null) continue;

                //Check if it references AutoDI. If it doesn't we will skip
                if (!matchesAssembly && assembly.MainModule.AssemblyReferences.All(a =>
                        a.FullName != typeof(DependencyAttribute).Assembly.FullName))
                {
                    continue;
                }
                //Either references AutoDI, or was a config assembly match, include the types.
                foreach (TypeDefinition type in FilterTypes(assembly.MainModule.GetAllTypes()))
                {
                    allTypes.Add(type);
                }
            }
        }
        return allTypes;
    }


    private void ProcessType(TypeDefinition type)
    {
        foreach (MethodDefinition ctor in type.Methods.Where(x => x.IsConstructor))
        {
            ProcessConstructor(type, ctor);
        }
    }

    private void ProcessConstructor(TypeDefinition type, MethodDefinition constructor)
    {
        List<ParameterDefinition> dependencyParameters = constructor.Parameters.Where(
                        p => p.CustomAttributes.Any(a => a.AttributeType.IsType<DependencyAttribute>())).ToList();

        List<PropertyDefinition> dependencyProperties = type.Properties.Where(
            p => p.CustomAttributes.Any(a => a.AttributeType.IsType<DependencyAttribute>())).ToList();

        if (dependencyParameters.Any() || dependencyProperties.Any())
        {
            var resolverType = ModuleDefinition.Get<IDependencyResolver>();
            var dependencyResolverType = ModuleDefinition.ImportReference(typeof(DependencyResolver));

            var injector = new Injector(constructor);

            var end = Instruction.Create(OpCodes.Nop);

            var resolverVariable = new VariableDefinition(resolverType);
            constructor.Body.Variables.Add(resolverVariable);

            //Get the IDependencyResolver by calling DependencyResolver.Get()
            injector.Insert(OpCodes.Call,
                new MethodReference(nameof(DependencyResolver.Get), resolverType, dependencyResolverType));

            //Store the resolver in our local variable
            injector.Insert(OpCodes.Stloc, resolverVariable);
            //Push the resolver on top of the stack
            injector.Insert(OpCodes.Ldloc, resolverVariable);
            //Branch to the end if the resolver is null
            injector.Insert(OpCodes.Brfalse, end);

            foreach (ParameterDefinition parameter in dependencyParameters)
            {
                if (!parameter.IsOptional)
                {
                    LogInfo(
                        $"Constructor parameter {parameter.ParameterType.Name} {parameter.Name} is marked with {nameof(DependencyAttribute)} but is not an optional parameter. In {type.FullName}.");
                }
                if (parameter.Constant != null)
                {
                    LogWarning(
                        $"Constructor parameter {parameter.ParameterType.Name} {parameter.Name} in {type.FullName} does not have a null default value. AutoDI will only resolve dependencies that are null");
                }

                ResolveDependency(parameter.ParameterType, parameter,
                    new[] { Instruction.Create(OpCodes.Ldarg, parameter) },
                    null,
                    Instruction.Create(OpCodes.Starg, parameter));
            }


            foreach (PropertyDefinition property in dependencyProperties)
            {
                FieldDefinition backingField = null;
                //Store the return from the resolve method in the method parameter
                if (property.SetMethod == null)
                {
                    //TODO: Constant string, compiler detail... yuck yuck and double duck
                    backingField = property.DeclaringType.Fields.FirstOrDefault(f => f.Name == $"<{property.Name}>k__BackingField");
                    if (backingField == null)
                    {
                        LogWarning(
                            $"{property.FullName} is marked with {nameof(DependencyAttribute)} but cannot be set. Dependency properties must either be auto properties or have a setter");
                        continue;
                    }
                }

                //injector.Insert(OpCodes.Call, property.GetMethod);
                ResolveDependency(property.PropertyType, property,
                    new[]
                    {
                        Instruction.Create(OpCodes.Ldarg_0),
                        Instruction.Create(OpCodes.Call, property.GetMethod),
                    },
                    Instruction.Create(OpCodes.Ldarg_0),
                    property.SetMethod != null
                        ? Instruction.Create(OpCodes.Call, property.SetMethod)
                        : Instruction.Create(OpCodes.Stfld, backingField));
            }


            injector.Insert(end);


            void ResolveDependency(TypeReference dependencyType, ICustomAttributeProvider source, 
                IEnumerable<Instruction> loadSource, 
                Instruction resolveAssignmentTarget,
                Instruction setResult)
            {
                //Push dependency parameter onto the stack
                injector.Insert(loadSource);
                var afterParam = Instruction.Create(OpCodes.Nop);
                //Push null onto the stack
                injector.Insert(OpCodes.Ldnull);
                //Push 1 if the values are equal, 0 if they are not equal
                injector.Insert(OpCodes.Ceq);
                //Branch if the value is false (0), the dependency was set by the caller we wont replace it
                injector.Insert(OpCodes.Brfalse_S, afterParam);
                //Push the dependency resolver onto the stack
                if (resolveAssignmentTarget != null)
                {
                    injector.Insert(resolveAssignmentTarget);
                }
                injector.Insert(OpCodes.Ldloc, resolverVariable);

                //Create parameters array
                var dependencyAttribute = source.CustomAttributes.First(x => x.AttributeType.IsType<DependencyAttribute>());
                var values =
                    (dependencyAttribute.ConstructorArguments?.FirstOrDefault().Value as CustomAttributeArgument[])
                    ?.Select(x => x.Value)
                    .OfType<CustomAttributeArgument>()
                    .ToArray();
                //Create array of appropriate length
                injector.Insert(OpCodes.Ldc_I4, values?.Length ?? 0);
                injector.Insert(OpCodes.Newarr, ModuleDefinition.ImportReference(typeof(object)));
                if (values?.Length > 0)
                {
                    for (int i = 0; i < values.Length; ++i)
                    {
                        injector.Insert(OpCodes.Dup);
                        //Push the array index to insert
                        injector.Insert(OpCodes.Ldc_I4, i);
                        //Insert constant value with any boxing/conversion needed
                        InsertObjectConstant(injector, values[i].Value, values[i].Type.Resolve());
                        //Push the object into the array at index
                        injector.Insert(OpCodes.Stelem_Ref);
                    }
                }

                //Call the resolve method
                var resolveMethod = ModuleDefinition.ImportReference(
                    typeof(IDependencyResolver).GetMethod(nameof(IDependencyResolver.Resolve)));
                resolveMethod = new GenericInstanceMethod(resolveMethod)
                {
                    GenericArguments = { ModuleDefinition.ImportReference(dependencyType) }
                };
                injector.Insert(OpCodes.Callvirt, resolveMethod);
                //Set the return from the resolve method into the parameter
                injector.Insert(setResult);
                injector.Insert(afterParam);
            }
        }

        constructor.Body.OptimizeMacros();

    }

    private void InsertObjectConstant(Injector injector, object constant, TypeDefinition type)
    {
        if (ReferenceEquals(constant, null))
        {
            injector.Insert(OpCodes.Ldnull);
            return;
        }
        if (type.IsEnum)
        {
            InsertAndBoxConstant(injector, constant, type.GetEnumUnderlyingType(), type);
        }
        else
        {
            var typeDef = ModuleDefinition.ImportReference(constant.GetType());
            InsertAndBoxConstant(injector, constant, typeDef, constant is string ? null : typeDef);
        }
    }

    private void InsertAndBoxConstant(Injector injector, object constant, TypeReference type, TypeReference boxType = null)
    {
        if (type.IsType<string>())
        {
            injector.Insert(OpCodes.Ldstr, (string)constant);
        }
        else if (type.IsType<int>())
        {
            injector.Insert(OpCodes.Ldc_I4, (int)constant);
        }
        else if (type.IsType<long>())
        {
            injector.Insert(OpCodes.Ldc_I8, (long)constant);
        }
        else if (type.IsType<double>())
        {
            injector.Insert(OpCodes.Ldc_R8, (double)constant);
        }
        else if (type.IsType<float>())
        {
            injector.Insert(OpCodes.Ldc_R4, (float)constant);
        }
        else if (type.IsType<short>())
        {
            injector.Insert(OpCodes.Ldc_I4, (short)constant);
        }
        else if (type.IsType<byte>())
        {
            injector.Insert(OpCodes.Ldc_I4, (byte)constant);
        }
        else if (type.IsType<uint>())
        {
            injector.Insert(OpCodes.Ldc_I4, (int)(uint)constant);
        }
        else if (type.IsType<ulong>())
        {
            injector.Insert(OpCodes.Ldc_I8, (long)(ulong)constant);
        }
        else if (type.IsType<ushort>())
        {
            injector.Insert(OpCodes.Ldc_I4, (ushort)constant);
        }
        else if (type.IsType<sbyte>())
        {
            injector.Insert(OpCodes.Ldc_I4, (sbyte)constant);
        }
        if (boxType != null)
        {
            injector.Insert(Instruction.Create(OpCodes.Box, boxType));
        }
        LogWarning($"Unknown constant type {constant.GetType().FullName}");
    }

    // Will be called when a request to cancel the build occurs. OPTIONAL
    public void Cancel()
    {
    }

    // Will be called after all weaving has occurred and the module has been saved. OPTIONAL
    public void AfterWeaving()
    {
    }
}

