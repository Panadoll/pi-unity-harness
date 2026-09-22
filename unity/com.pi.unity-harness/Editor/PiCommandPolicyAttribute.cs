using System;

namespace Pi.UnityHarness.Editor
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    internal sealed class PiCommandPolicyAttribute : Attribute
    {
        public string Mutability { get; set; } = "write";

        public PiCommandPolicyAttribute(string mutability = "write")
        {
            Mutability = mutability;
        }
    }
}
