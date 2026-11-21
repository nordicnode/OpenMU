// <copyright file="AssemblyInfo.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.CompilerServices;

// The rate limit partition key is internal, because it is an implementation detail of the MCP endpoint which
// the tests need to verify directly: the request pipeline in front of it only lets authenticated or proxied
// traffic through, so an anonymous caller's bucket cannot be observed over HTTP.
[assembly: InternalsVisibleTo("MUnique.OpenMU.Web.Tests")]
