using System.Runtime.CompilerServices;

// The transport's argument construction and output extraction are internal implementation
// details, but they are exactly what needs pinning by tests — a silent change to either
// breaks every station at once.
[assembly: InternalsVisibleTo("Factory.Tests")]
