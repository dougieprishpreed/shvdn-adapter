using System;

// These attributes are only markers read by the patcher (matched by name). They are compiled into each
// shim assembly but never end up in the patched SHVDN API assembly.
namespace ShvdnCompat
{
    /// <summary>
    /// Marks a static class whose <see cref="ShimAttribute"/> methods are added to the API type named
    /// <see cref="Target"/> (e.g. "GTA.UI.Notification", nested types use '/' like "GTA.Foo/Bar").
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    internal sealed class ShimTypeAttribute : Attribute
    {
        public ShimTypeAttribute(string target) => Target = target;
        public string Target { get; }
    }

    /// <summary>
    /// Marks a method that is copied into the target API type when the target does not already contain a
    /// member with the exact same signature. With <see cref="Instance"/> set, the first parameter is
    /// dropped and becomes <c>this</c> (use <c>ref T</c> for structs).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    internal sealed class ShimAttribute : Attribute
    {
        public bool Instance { get; set; }

        /// <summary>Optional IL name override, for names C# cannot declare twice in one class.</summary>
        public string Name { get; set; }
    }

    /// <summary>
    /// Marks a type that is cloned wholesale into the API assembly under <see cref="FullName"/>
    /// when the API assembly does not already define a type with that name.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
    internal sealed class InjectTypeAttribute : Attribute
    {
        public InjectTypeAttribute(string fullName) => FullName = fullName;
        public string FullName { get; }
    }
}
