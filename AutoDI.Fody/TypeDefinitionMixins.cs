﻿using System.Linq;
using System.Runtime.CompilerServices;
using Mono.Cecil;

namespace AutoDI.Fody
{
    internal static class TypeDefinitionMixins
    {
        public static bool IsCompilerGenerated(this TypeDefinition type)
        {
            return type.HasCustomAttributes &&
                   type.CustomAttributes.Any(a => string.Equals(a.AttributeType.FullName,
                       typeof(CompilerGeneratedAttribute).FullName));
        }
    }
}