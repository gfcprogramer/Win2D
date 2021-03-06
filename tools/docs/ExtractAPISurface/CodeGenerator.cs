﻿// Copyright (c) Microsoft Corporation. All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License"); you may
// not use these files except in compliance with the License. You may obtain
// a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS, WITHOUT
// WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the
// License for the specific language governing permissions and limitations
// under the License.

using System;
using System.Linq;
using System.Collections.Generic;
using System.Xml.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.IO;

namespace ExtractAPISurface
{
    class CodeGenerator : IDisposable
    {
        CommandLineOptions options;
        AssemblyCollection assemblies;
        Assembly assembly;
        CodeWriter output;

        // Track all the types we have seen used as parameters, return values, base classes, etc.
        HashSet<Type> seenTypes = new HashSet<Type>();

        static HashSet<Type> placeholdersWritten = new HashSet<Type>();


        public CodeGenerator(CommandLineOptions options, AssemblyCollection assemblies, Assembly assembly)
        {
            this.options = options;
            this.assemblies = assemblies;
            this.assembly = assembly;

            output = new CodeWriter(options.OutputPath, assembly.GetName().Name + ".cs");
        }


        public void Dispose()
        {
            output.Dispose();
        }


        public void GenerateAPISurface(Assembly assembly)
        {
            output.WriteLine("// generated by ExtractAPISurface tool");
            output.WriteSeparator();

            var publicTypes = from type in assembly.DefinedTypes
                              where type.IsPublic
                              select type;

            ValidatePublicTypes(publicTypes);
            WriteTypesByNamespace(publicTypes, WriteType);
            WriteReferencedTypePlaceholders();
        }


        void WriteType(TypeInfo type)
        {
            output.WriteDocComment();

            if (type.IsEnum)
            {
                WriteEnum(type);
            }
            else if (type.IsValueType)
            {
                WriteStruct(type);
            }
            else if (type.IsInterface)
            {
                WriteInterface(type);
            }
            else if (type.IsClass && !type.IsDelegate())
            {
                WriteClass(type);
            }
            else
            {
                throw new NotImplementedException("Unknown type of type.");
            }

            output.WriteSeparator();
        }


        void WriteEnum(TypeInfo type)
        {
            if (type.HasAttribute<FlagsAttribute>())
            {
                output.WriteLine("[System.Flags]");
            }

            var enumBaseTypes = new Dictionary<Type, string>
            {
                { typeof(int), "int" },
                { typeof(uint), "uint" },
            };

            output.WriteLine("public enum {0} : {1}", type.Name, enumBaseTypes[type.GetEnumUnderlyingType()]);

            using (output.WriteBraces())
            {
                var enumValues = from field in type.GetFields()
                                 where field.Name != "value__"
                                 select new { field.Name, Value = field.GetRawConstantValue() };

                foreach (var value in enumValues)
                {
                    output.WriteDocComment();
                    output.WriteLine("{0} = {1},", value.Name, value.Value);
                }
            }
        }


        void WriteStruct(TypeInfo type)
        {
            output.WriteLine("public struct {0}", type.Name);

            using (output.WriteBraces())
            {
                WriteTypeBody(type);
            }
        }


        void WriteInterface(TypeInfo type)
        {
            output.WriteLine("public interface {0}{1}", type.Name, FormatBaseTypes(type));

            using (output.WriteBraces())
            {
                WriteTypeBody(type);
            }
        }


        void WriteClass(TypeInfo type)
        {
            if (!type.IsSealed)
            {
                throw new NotImplementedException("Huh, shouldn't all WinRT runtime classes be sealed?");
            }

            // .NET lacks a dedicated flag for static classes, so it represents them by combining IsSealed and IsAbstract.
            bool isStatic = type.IsAbstract;

            string modifier = isStatic ? "static" : "sealed";

            output.WriteLine("public {0} class {1}{2}", modifier, type.Name, FormatBaseTypes(type));

            using (output.WriteBraces())
            {
                // If the type has no public constructors, give it an internal one. Otherwise
                // the C# compiler will helpfully insert an unwanted public default constructor.
                if (!isStatic && !type.DeclaredConstructors.Any(constructor => constructor.IsPublic))
                {
                    output.WriteLine("internal {0}() {{ }}", type.Name);
                    output.WriteSeparator();
                }

                WriteTypeBody(type);
            }
        }


        void WriteTypeBody(TypeInfo type)
        {
            WriteMembers(type.DeclaredConstructors, constructor => constructor.IsPublic, DocumentConstructor, WriteConstructor);
            WriteMembers(type.DeclaredMethods,      WantMethod,                          DocumentMethod,      WriteMethod);
            WriteMembers(type.DeclaredProperties,   property => property.IsPublic(),     null,                WriteProperty);
            WriteMembers(type.DeclaredFields,       field => field.IsPublic,             null,                WriteField);
            WriteMembers(type.DeclaredEvents,       eventInfo => eventInfo.IsPublic(),   null,                WriteEvent);

            if (type.DeclaredNestedTypes.Any(nestedType => nestedType.IsPublic))
            {
                throw new NotImplementedException("Don't support nested types.");
            }
        }


        void WriteMembers<T>(IEnumerable<T> members, Func<T, bool> wantMember, Func<T, string> documentMember, Action<T> writeMember)
        {
            foreach (var member in members.Where(wantMember))
            {
                output.WriteDocComment(documentMember != null ? documentMember(member) : null);

                writeMember(member);

                output.WriteSeparator();
            }
        }


        void WriteConstructor(ConstructorInfo constructor)
        {
            if (constructor.IsStatic)
            {
                throw new NotImplementedException("Don't support static constructors.");
            }

            output.WriteLine("public {0}({1}){2}", constructor.DeclaringType.Name,
                                                   FormatParameterList(constructor.GetParameters()),
                                                   FormatMethodBody(constructor.DeclaringType));
        }


        static bool WantMethod(MethodInfo method)
        {
            // We never want non-public methods.
            if (!method.IsPublic)
            {
                return false;
            }

            if (method.IsSpecialName)
            {
                // Ignore property getters/setters and event add/remove handlers.
                string[] ignorePrefixes =
                {
                    "get_",
                    "set_",
                    "put_",
                    "add_",
                    "remove_",
                };

                if (ignorePrefixes.Any(prefix => method.Name.StartsWith(prefix)))
                {
                    return false;
                }

                // We do want operator overloads.
                string[] wantPrefixes =
                {
                    "op_",
                };

                if (wantPrefixes.Any(prefix => method.Name.StartsWith(prefix)))
                {
                    return true;
                }

                // Don't recognize this special method.
                throw new NotImplementedException(string.Format("Unknown special method name '{0}", method.Name));
            }

            // Ok, sure, we should output this method.
            return true;
        }


        void WriteMethod(MethodInfo method)
        {
            var methodName = method.Name;
            var returnType = FormatTypeName(method.ReturnType);

            if (method.IsSpecialName)
            {
                switch (method.Name)
                {
                    case "op_Addition":      methodName = "operator +";  break;
                    case "op_Subtraction":   methodName = "operator -";  break;
                    case "op_Multiply":      methodName = "operator *";  break;
                    case "op_Division":      methodName = "operator /";  break;
                    case "op_UnaryNegation": methodName = "operator -";  break;
                    case "op_Equality":      methodName = "operator =="; break;
                    case "op_Inequality":    methodName = "operator !="; break;

                    case "op_Implicit":
                        methodName = returnType;
                        returnType = "implicit operator";
                        break;

                    default:
                        throw new NotImplementedException(string.Format("Unknown special method name '{0}", method.Name));
                }
            }

            output.WriteLine("{0}{1} {2}({3}){4}", FormatModifiers(method),
                                                   returnType,
                                                   methodName,
                                                   FormatParameterList(method.GetParameters()),
                                                   FormatMethodBody(method.DeclaringType));
        }


        void WriteProperty(PropertyInfo property)
        {
            if (property.GetIndexParameters().Any())
            {
                throw new NotImplementedException("Don't support indexed properties.");
            }

            output.WriteLine("{0}{1} {2}", FormatModifiers(property.GetMethod),
                                           FormatTypeName(property.PropertyType),
                                           property.Name);

            using (output.WriteBraces())
            {
                if (property.CanRead)
                {
                    output.WriteLine("get" + FormatMethodBody(property.DeclaringType));
                }

                if (property.CanWrite)
                {
                    output.WriteLine("set" + FormatMethodBody(property.DeclaringType));
                }
            }
        }


        void WriteField(FieldInfo field)
        {
            output.WriteLine("{0}{1} {2};", FormatModifiers(field.DeclaringType, field.IsStatic),
                                            FormatTypeName(field.FieldType),
                                            field.Name);
        }


        void WriteEvent(EventInfo eventInfo)
        {
            output.WriteLine("{0}event {1} {2};", FormatModifiers(eventInfo.AddMethod),
                                                  FormatTypeName(eventInfo.EventHandlerType),
                                                  eventInfo.Name);
        }


        // Gets the " : BaseClass, InterfaceList" part that goes after a class definition.
        string FormatBaseTypes(TypeInfo type)
        {
            var baseTypes = new Type[] { type.BaseType }.Concat(type.ImplementedInterfaces);

            var baseNames = from baseType in baseTypes
                            where baseType != null && baseType.IsPublic
                            select FormatTypeName(baseType);

            if (baseNames.Any())
            {
                return " : " + string.Join(", ", baseNames);
            }
            else
            {
                return string.Empty;
            }
        }


        string FormatParameterList(ParameterInfo[] parameters)
        {
            var formattedParameters = from parameter in parameters
                                      select FormatParameter(parameter);

            return string.Join(", ", formattedParameters);
        }


        string FormatParameter(ParameterInfo parameter)
        {
            // Sanity check.
            if (parameter.IsRetval || parameter.IsOptional)
            {
                throw new NotImplementedException("This is a strange parameter. I don't like it.");
            }

            var parameterType = parameter.ParameterType;
            string modifier = string.Empty;

            // Handle extension methods.
            if (parameter.Position == 0 && parameter.Member.HasAttribute<ExtensionAttribute>())
            {
                modifier += "this ";
            }
            
            // Handle out/ref modifiers.
            if (parameterType.IsByRef)
            {
                modifier += parameter.IsOut ? "out " : "ref ";
                parameterType = parameterType.GetElementType();
            }

            // Format the parameter type and name.
            return modifier + FormatTypeName(parameterType) + ' ' + parameter.Name;
        }


        string FormatTypeName(Type type)
        {
            // Sanity check.
            if (type.IsPointer)
            {
                throw new NotImplementedException("Nooo! I liketh not this crazy type of type.");
            }

            // Record that we have seen this type.
            seenTypes.Add(type.IsGenericType ? type.GetGenericTypeDefinition() : type);

            // C# requires System.Void to be spelled as just "void".
            if (type == typeof(void))
            {
                return "void";
            }

            // Format the basic type name.
            string name = "global::" + type.Namespace + '.' + type.Name;

            // Format any generic type arguments.
            if (type.IsGenericType)
            {
                var genericArguments = from genericArgument in type.GenericTypeArguments
                                       select FormatTypeName(genericArgument);

                name =  FormatGenericTypeName(name, genericArguments);
            }

            return name;
        }


        static string FormatGenericTypeName(string typeName, IEnumerable<string> genericArguments)
        {
            // Remove the `1 suffix that the CLR puts on the names of generic types.
            string strippedName = typeName.Remove(typeName.IndexOf('`'));

            return strippedName + '<' + string.Join(", ", genericArguments) + '>';
        }


        static string FormatModifiers(Type declaringType, bool isStatic)
        {
            string result = string.Empty;

            // Everything is public, except interface members are public by default and C# doesn't let you explicitly specify that.
            if (!declaringType.IsInterface)
            {
                result += "public ";
            }

            // Is this member static?
            if (isStatic)
            {
                result += "static ";
            }

            return result;
        }


        static string FormatModifiers(MethodInfo method)
        {
            string result = FormatModifiers(method.DeclaringType, method.IsStatic);

            // Is this member an override?
            if (method.GetBaseDefinition() != method)
            {
                result += "override ";
            }

            return result;
        }


        static string FormatMethodBody(Type declaringType)
        {
            if (declaringType.IsInterface)
            {
                // Interfaces don't have method bodies.
                return ";";
            }
            else
            {
                // Throwing avoids having to generate different code per return type vs. void.
                return " { throw new System.NotImplementedException(); }";
            }
        }


        // Automatically fill in formulaic documentation text, to save time for those writing the actual docs.
        static string DocumentConstructor(ConstructorInfo constructor)
        {
            string typeOfType = constructor.DeclaringType.IsClass ? "class" : "structure";

            return string.Format("Initializes a new instance of the {0} {1}.", constructor.DeclaringType.Name, typeOfType);
        }


        // Automatically fill in formulaic documentation text, to save time for those writing the actual docs.
        static string DocumentMethod(MethodInfo method)
        {
            switch (method.Name)
            {
                case "Dispose":
                    return string.Format("Releases all resources used by the {0}.", method.DeclaringType.Name);

                default:
                    return null;
            }
        }


        // Writes dummy versions of WinRT system types, which are not part of our API but needed to compile the generated C#.
        void WriteReferencedTypePlaceholders()
        {
            // The CLR maps some special WinRT types to equivalents from this assembly, so these count as
            // WinRT references even though we can't tell that from relecting over the .NET version of the type.
            const string magicWinRTAssembly = "System.Runtime.WindowsRuntime";

            var placeholders = (from type in seenTypes
                                where assemblies.TypeIsFromReferenceAssembly(type) || type.Assembly.GetName().Name == magicWinRTAssembly
                                where !placeholdersWritten.Contains(type)
                                select type).ToList();

            WriteTypesByNamespace(placeholders, WriteReferencedTypePlaceholder);

            WriteReferencedTypeXmlDocs(placeholders);
        }


        void WriteReferencedTypePlaceholder(Type type)
        {
            // Get the type name.
            var name = type.Name;

            if (type.IsGenericType)
            {
                var genericArguments = from i in Enumerable.Range(0, type.GetGenericArguments().Length)
                                       select "T" + i;

                name = FormatGenericTypeName(name, genericArguments);
            }

            // Write an empty type placeholder.
            if (type.IsDelegate())
            {
                output.WriteLine("public delegate void {0}();", name);
            }
            else if (type.IsEnum)
            {
                output.WriteLine("public enum {0} {{ }}", name);
            }
            else if (type.IsValueType)
            {
                output.WriteLine("public struct {0} {{ }}", name);
            }
            else if (type.IsInterface)
            {
                output.WriteLine("public interface {0} {{ }}", name);
            }
            else
            {
                output.WriteLine("public class {0} {{ internal {0}() {{ }} }}", name);
            }

            // Record that we have seen this type.
            placeholdersWritten.Add(type);
        }


        // Generates XML docs linking our placeholder types to their real documentation on MSDN.
        void WriteReferencedTypeXmlDocs(IEnumerable<Type> placeholderTypes)
        {
            var xml = new XDocument(
                new XElement("doc",
                    new XElement("assembly",
                        new XElement("name", assembly.GetName().Name)
                    ),
                    new XElement("members",
                        from type in placeholderTypes
                        select new XElement("member",
                            new XAttribute("name", "T:" + type.FullName),
                            new XElement("tocexclude"),
                            new XElement("summary",
                                new XElement("b",
                                    new XElement("a",
                                        new XAttribute("href", GetReferencedTypeMsdnUrl(type.FullName)),
                                        "This type is documented on MSDN."
                                    )
                                )
                            )
                        )
                    )
                )
            );

            xml.Save(Path.Combine(options.OutputPath, assembly.GetName().Name + ".placeholders.xml"));
        }


        // Generates XML doc summaries for placeholder namespaces.
        public static void WritePlaceholderNamespaceSummaries(CommandLineOptions options)
        {
            var placeholderNamespaces = from ns in placeholdersWritten.Select(type => type.Namespace).Distinct()
                                        select new XElement("member",
                                            new XAttribute("name", "N:" + ns),
                                            new XElement("summary",
                                                new XElement("a",
                                                    new XAttribute("href", GetReferencedTypeMsdnUrl(ns)),
                                                    "This namespace is documented on MSDN."
                                                )
                                            )
                                        );

            var xml = new XDocument(
                new XElement("doc",
                    new XElement("members",
                        placeholderNamespaces
                    )
                )
            );

            xml.Save(Path.Combine(options.OutputPath, "PlaceholderNamespaces.xml"));
        }


        static string GetReferencedTypeMsdnUrl(string typeName)
        {
            const string msdnPrefix = "http://msdn.microsoft.com/library/windows/apps/";

            if (typeName.Contains('`'))
            {
                // MSDN uses weird mangled URLs for generic types, so we just hardcode the ones we care about.
                if (typeName == "Windows.Foundation.TypedEventHandler`2")
                {
                    return msdnPrefix + "br225997";
                }
                else if (typeName == "Windows.Foundation.IAsyncOperation`1")
                {
                    return msdnPrefix + "br206598";
                }
                else
                {
                    throw new NotSupportedException(string.Format("Please add MSDN link for new generic type {0} to GetReferencedTypeMsdnUrl method in CodeGenerator.cs.", typeName));
                }
            }
            else
            {
                // Non generic type URLs are simple and consistent.
                return msdnPrefix + typeName;
            }
        }


        // Groups a set of Type or TypeInfo objects by their namespace, writes out the containing
        // namespace blocks, then invokes a callback to write out the actual contents of each type.
        void WriteTypesByNamespace<T>(IEnumerable<T> types, Action<T> writeType) where T : Type
        {
            var typesByNamespace = from type in types
                                   orderby type.Name
                                   group type by type.Namespace into namespaces
                                   orderby namespaces.Key
                                   select namespaces;

            foreach (var group in typesByNamespace)
            {
                output.WriteLine("namespace {0}", group.Key);

                using (output.WriteBraces())
                {
                    foreach (var type in group)
                    {
                        writeType(type);
                    }
                }

                output.WriteSeparator();
            }
        }
        
        
        static void ValidatePublicTypes(IEnumerable<TypeInfo> publicTypes)
        {
            // Warn if any WinRT factory or static interfaces were accidentally left visible.
            string[] specialInterfaceNames = 
            {
                "I{0}Factory",
                "I{0}Statics",
            };

            var potentialNames = from type in publicTypes
                                 where type.IsClass
                                 from name in specialInterfaceNames
                                 select string.Format(name, type.Name);

            var foundNames = from name in potentialNames
                             where publicTypes.Any(type => type.Name == name)
                             select name;

            foreach (var name in foundNames)
            {
                Console.WriteLine("Warning: special interface {0} should be marked exclusiveto.", name);
            }
        }
    }
}
