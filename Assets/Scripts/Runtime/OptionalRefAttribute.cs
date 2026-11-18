using System;

namespace HoldTheTable
{
    /// <summary>Marks a serialized reference that the Validator may accept as null.</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class OptionalRefAttribute : Attribute { }
}
