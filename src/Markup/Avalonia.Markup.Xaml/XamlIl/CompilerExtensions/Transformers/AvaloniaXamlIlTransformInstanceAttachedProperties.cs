using System.Collections.Generic;
using System.Linq;
using XamlIl;
using XamlIl.Ast;
using XamlIl.Transform;
using XamlIl.TypeSystem;

namespace Avalonia.Markup.Xaml.XamlIl.CompilerExtensions.Transformers
{
    public class AvaloniaXamlIlTransformInstanceAttachedProperties : IXamlIlAstTransformer
    {

        public IXamlIlAstNode Transform(XamlIlAstTransformationContext context, IXamlIlAstNode node)
        {
            if (node is XamlIlAstNamePropertyReference prop 
                && prop.TargetType is XamlIlAstClrTypeReference targetRef 
                && prop.DeclaringType is XamlIlAstClrTypeReference declaringRef)
            {
                // Target and declared type aren't assignable but both inherit from AvaloniaObject
                var avaloniaObject = context.Configuration.TypeSystem.FindType("Avalonia.AvaloniaObject");
                if (avaloniaObject.IsAssignableFrom(targetRef.Type)
                    && avaloniaObject.IsAssignableFrom(declaringRef.Type)
                    && !targetRef.Type.IsAssignableFrom(declaringRef.Type))
                {
                    // Instance property
                    var clrProp = declaringRef.Type.GetAllProperties().FirstOrDefault(p => p.Name == prop.Name);
                    if (clrProp != null
                        && (clrProp.Getter?.IsStatic == false || clrProp.Setter?.IsStatic == false))
                    {
                        var declaringType = (clrProp.Getter ?? clrProp.Setter)?.DeclaringType;
                        var avaloniaPropertyFieldName = prop.Name + "Property";
                        var avaloniaPropertyField = declaringType.Fields.FirstOrDefault(f => f.IsStatic && f.Name == avaloniaPropertyFieldName);
                        if (avaloniaPropertyField != null)
                        {
                            var avaloniaPropertyType = avaloniaPropertyField.FieldType;
                            while (avaloniaPropertyType != null
                                   && !(avaloniaPropertyType.Namespace == "Avalonia"
                                        && (avaloniaPropertyType.Name == "AvaloniaProperty"
                                            || avaloniaPropertyType.Name == "AvaloniaProperty`1"
                                        )))
                            {
                                // Attached properties are handled by vanilla XamlIl
                                if (avaloniaPropertyType.Name.StartsWith("AttachedProperty"))
                                    return node;
                                
                                avaloniaPropertyType = avaloniaPropertyType.BaseType;
                            }

                            if (avaloniaPropertyType == null)
                                return node;

                            if (avaloniaPropertyType.GenericArguments?.Count > 1)
                                return node;

                            var propertyType = avaloniaPropertyType.GenericArguments?.Count == 1 ?
                                avaloniaPropertyType.GenericArguments[0] :
                                context.Configuration.WellKnownTypes.Object;

                            return new XamlIlAstClrPropertyReference(prop,
                                new AvaloniaAttachedInstanceProperty(prop.Name, context.Configuration,
                                    declaringType, propertyType, avaloniaPropertyType, avaloniaObject,
                                    avaloniaPropertyField));
                        }

                    }


                }
            }

            return node;
        }

        class AvaloniaAttachedInstanceProperty : IXamlIlProperty
        {
            private readonly XamlIlTransformerConfiguration _config;
            private readonly IXamlIlType _declaringType;
            private readonly IXamlIlType _avaloniaPropertyType;
            private readonly IXamlIlType _avaloniaObject;
            private readonly IXamlIlField _field;

            public AvaloniaAttachedInstanceProperty(string name,
                XamlIlTransformerConfiguration config,
                IXamlIlType declaringType,
                IXamlIlType type,
                IXamlIlType avaloniaPropertyType,
                IXamlIlType avaloniaObject,
                IXamlIlField field)
            {
                _config = config;
                _declaringType = declaringType;
                _avaloniaPropertyType = avaloniaPropertyType;
                
                // XamlIl doesn't support generic methods yet
                if (_avaloniaPropertyType.GenericArguments?.Count > 0)
                    _avaloniaPropertyType = _avaloniaPropertyType.BaseType;
                
                _avaloniaObject = avaloniaObject;
                _field = field;
                Name = name;
                PropertyType = type;
                Setter = new SetterMethod(this);
                Getter = new GetterMethod(this);
            }

            public bool Equals(IXamlIlProperty other) =>
                other is AvaloniaAttachedInstanceProperty ap && ap._field.Equals(_field);

            public string Name { get; }
            public IXamlIlType PropertyType { get; }
            public IXamlIlMethod Setter { get; }
            public IXamlIlMethod Getter { get; }
            public IReadOnlyList<IXamlIlCustomAttribute> CustomAttributes { get; } = new List<IXamlIlCustomAttribute>();

            class Method
            {
                public AvaloniaAttachedInstanceProperty Parent { get; }
                public bool IsPublic => true;
                public bool IsStatic => true;
                public string Name { get; protected set; }
                public IXamlIlType DeclaringType { get; }
                public Method(AvaloniaAttachedInstanceProperty parent)
                {
                    Parent = parent;
                    DeclaringType = parent._declaringType;
                }

                public bool Equals(IXamlIlMethod other) =>
                    other is Method m && m.Name == Name && m.DeclaringType.Equals(DeclaringType);
            }
            
            class SetterMethod : Method, IXamlIlCustomEmitMethod
            {
                public SetterMethod(AvaloniaAttachedInstanceProperty parent) : base(parent)
                {
                    Name = "AvaloniaObject:SetValue_" + Parent.Name;
                    Parameters = new[] {Parent._avaloniaObject, Parent.PropertyType};
                }

                public IXamlIlType ReturnType => Parent._config.WellKnownTypes.Void;
                public IReadOnlyList<IXamlIlType> Parameters { get; }
                
                public void EmitCall(IXamlIlEmitter emitter)
                {
                    var so = Parent._config.WellKnownTypes.Object;
                    var method = Parent._avaloniaObject
                        .FindMethod(m => m.IsPublic && !m.IsStatic && m.Name == "SetValue"
                                         &&
                                         m.Parameters.Count == 3
                                         && m.Parameters[0].Equals(Parent._avaloniaPropertyType)
                                         && m.Parameters[1].Equals(so)
                                         && m.Parameters[2].IsEnum
                        );
                    if (method == null)
                        throw new XamlIlTypeSystemException(
                            "Unable to find SetValue(AvaloniaProperty, object, BindingPriority) on AvaloniaObject");
                    var loc = emitter.DefineLocal(Parent.PropertyType);
                    emitter
                        .Stloc(loc)
                        .Ldsfld(Parent._field)
                        .Ldloc(loc);
                    if(Parent.PropertyType.IsValueType)
                        emitter.Box(Parent.PropertyType);
                    emitter        
                        .Ldc_I4(0)
                        .EmitCall(method);

                }
            }

            class GetterMethod : Method, IXamlIlCustomEmitMethod
            {
                public GetterMethod(AvaloniaAttachedInstanceProperty parent) : base(parent)
                {
                    Name = "AvaloniaObject:GetValue_" + Parent.Name;
                    Parameters = new[] {parent._avaloniaObject};
                }

                public IXamlIlType ReturnType => Parent.PropertyType;
                public IReadOnlyList<IXamlIlType> Parameters { get; }
                public void EmitCall(IXamlIlEmitter emitter)
                {
                    var method = Parent._avaloniaObject
                        .FindMethod(m => m.IsPublic && !m.IsStatic && m.Name == "GetValue"
                                         &&
                                         m.Parameters.Count == 1
                                         && m.Parameters[0].Equals(Parent._avaloniaPropertyType));
                    if (method == null)
                        throw new XamlIlTypeSystemException(
                            "Unable to find T GetValue<T>(AvaloniaProperty<T>) on AvaloniaObject");
                    emitter
                        .Ldsfld(Parent._field)
                        .EmitCall(method);
                    if (Parent.PropertyType.IsValueType)
                        emitter.Unbox_Any(Parent.PropertyType);

                }
            }
        }
    }
}