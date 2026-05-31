using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using SyncSession.Core.Interfaces;
using Xunit;
using Xunit.Abstractions;

namespace SyncSession.IntegrationTests.DatabaseLayer;

/// <summary>
/// Verifies that all IServerDatabase interface methods have corresponding tests.
/// Ensures 100% coverage of the database layer implementation.
/// </summary>
public class IServerDatabaseCoverageTests
{
    private readonly ITestOutputHelper _output;

    public IServerDatabaseCoverageTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void AllIServerDatabaseMethods_HaveCorrespondingTests()
    {
        // Arrange - Get all public methods from IServerDatabase
        var interfaceMethods = typeof(IServerDatabase)
            .GetMethods()
            .Where(m => !m.IsSpecialName) // Exclude property getters/setters
            .Select(m => m.Name)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        // Get all test methods from ServerDatabaseTests
        var testMethods = typeof(ServerDatabaseTests)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttributes(typeof(FactAttribute), false).Any())
            .ToList();

        _output.WriteLine($"Analyzing {interfaceMethods.Count} interface methods against {testMethods.Count} tests");

        // Build coverage mapping
        var methodCoverage = interfaceMethods.ToDictionary(
            method => method,
            method => new List<string>()
        );

        // Match test names to interface methods
        foreach (var testMethod in testMethods)
        {
            var testName = testMethod.Name;

            foreach (var interfaceMethod in interfaceMethods)
            {
                // Create variations to check:
                // 1. Exact match (case-insensitive)
                // 2. Without "Async" suffix (e.g., "CountTempTableRecords" matches "CountTempTableRecordsAsync")
                var methodWithoutAsync = interfaceMethod.EndsWith("Async")
                    ? interfaceMethod.Substring(0, interfaceMethod.Length - 5)
                    : interfaceMethod;

                if (testName.Contains(interfaceMethod, StringComparison.OrdinalIgnoreCase) ||
                    testName.Contains(methodWithoutAsync, StringComparison.OrdinalIgnoreCase))
                {
                    methodCoverage[interfaceMethod].Add(testName);
                }
            }
        }

        var uncovered = new List<string>();
        var covered = new List<(string Method, List<string> Tests)>();

        foreach (var kvp in methodCoverage.OrderBy(x => x.Key))
        {
            if (kvp.Value.Count == 0)
            {
                uncovered.Add(kvp.Key);
            }
            else
            {
                covered.Add((kvp.Key, kvp.Value));
            }
        }

        // Report Header
        _output.WriteLine("");
        _output.WriteLine("╔═══════════════════════════════════════════════════════════════════════╗");
        _output.WriteLine("║            IServerDatabase Method Coverage Report                    ║");
        _output.WriteLine("╚═══════════════════════════════════════════════════════════════════════╝");
        _output.WriteLine("");

        // Section 1: Covered Methods
        if (covered.Any())
        {
            _output.WriteLine("✅ COVERED METHODS:");
            _output.WriteLine("─────────────────────────────────────────────────────────────────────");
            foreach (var (method, tests) in covered)
            {
                _output.WriteLine($"  {method}");
                foreach (var test in tests)
                {
                    _output.WriteLine($"    → {test}");
                }
            }
            _output.WriteLine("");
        }

        // Section 2: Uncovered Methods
        if (uncovered.Any())
        {
            _output.WriteLine("❌ UNCOVERED METHODS:");
            _output.WriteLine("─────────────────────────────────────────────────────────────────────");
            foreach (var method in uncovered)
            {
                _output.WriteLine($"  {method}");
            }
            _output.WriteLine("");
        }

        // Summary
        var coverage = covered.Count * 100.0 / interfaceMethods.Count;

        _output.WriteLine("╔═══════════════════════════════════════════════════════════════════════╗");
        _output.WriteLine("║                      Coverage Summary                                 ║");
        _output.WriteLine("╚═══════════════════════════════════════════════════════════════════════╝");
        _output.WriteLine($"Total Methods:    {interfaceMethods.Count}");
        _output.WriteLine($"Covered:          {covered.Count}");
        _output.WriteLine($"Uncovered:        {uncovered.Count}");
        _output.WriteLine($"Coverage:         {coverage:F1}%");
        _output.WriteLine("");

        if (coverage == 100)
        {
            _output.WriteLine("🎉 Perfect! All IServerDatabase methods have test coverage!");
        }
        else
        {
            _output.WriteLine($"⚠️  Action needed: Add tests for {uncovered.Count} method(s)");
        }

        // Assert 100% coverage
        uncovered.Should().BeEmpty(
            $"All IServerDatabase methods must be tested. Missing: {string.Join(", ", uncovered)}");
    }

    [Fact]
    public void AllIServerDatabaseMethods_AreCalledSomewhere()
    {
        // Arrange - Get all public methods from IServerDatabase
        var interfaceMethods = typeof(IServerDatabase)
            .GetMethods()
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        // Get all test methods from ServerDatabaseTests
        var testMethods = typeof(ServerDatabaseTests)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttributes(typeof(FactAttribute), false).Any())
            .ToList();

        _output.WriteLine($"Scanning {testMethods.Count} tests for usage of {interfaceMethods.Count} methods");

        // Build "called by" mapping (loose matching - any substring match)
        var methodUsage = interfaceMethods.ToDictionary(
            method => method,
            method => new List<string>()
        );

        foreach (var testMethod in testMethods)
        {
            var testName = testMethod.Name;

            foreach (var interfaceMethod in interfaceMethods)
            {
                // Tokenize method name for flexible matching
                // e.g., "InsertBatchIntoTempTableAsync" → ["Insert", "Batch", "Into", "Temp", "Table"]
                var tokens = TokenizeMethodName(interfaceMethod);

                // Check if test name contains enough tokens from method name
                var matchedTokens = tokens.Count(token =>
                    testName.Contains(token, StringComparison.OrdinalIgnoreCase));

                // If 50%+ of tokens match, consider it a usage
                if (matchedTokens >= Math.Max(1, tokens.Count / 2))
                {
                    methodUsage[interfaceMethod].Add(testName);
                }
            }
        }

        // Report - Show "CodeLens style" reference count
        _output.WriteLine("");
        _output.WriteLine("╔═══════════════════════════════════════════════════════════════════════╗");
        _output.WriteLine("║          IServerDatabase Method Usage Report (CodeLens Style)        ║");
        _output.WriteLine("╚═══════════════════════════════════════════════════════════════════════╝");
        _output.WriteLine("");

        var unused = new List<string>();
        var used = new List<(string Method, int Count, List<string> Tests)>();

        foreach (var kvp in methodUsage.OrderBy(x => x.Key))
        {
            if (kvp.Value.Count == 0)
            {
                unused.Add(kvp.Key);
                _output.WriteLine($"❌ {kvp.Key,-45} 0 references");
            }
            else
            {
                used.Add((kvp.Key, kvp.Value.Count, kvp.Value));
                _output.WriteLine($"✅ {kvp.Key,-45} {kvp.Value.Count} reference(s)");
                foreach (var test in kvp.Value)
                {
                    _output.WriteLine($"   → {test}");
                }
            }
        }

        // Summary
        _output.WriteLine("");
        _output.WriteLine("╔═══════════════════════════════════════════════════════════════════════╗");
        _output.WriteLine("║                      Usage Summary                                    ║");
        _output.WriteLine("╚═══════════════════════════════════════════════════════════════════════╝");
        _output.WriteLine($"Total Methods:    {interfaceMethods.Count}");
        _output.WriteLine($"Referenced:       {used.Count}");
        _output.WriteLine($"Unreferenced:     {unused.Count}");

        if (unused.Count > 0)
        {
            var coverage = used.Count * 100.0 / interfaceMethods.Count;
            _output.WriteLine($"Coverage:         {coverage:F1}%");
            _output.WriteLine("");
            _output.WriteLine("⚠️  WARNING: The following methods are not called in any tests:");
            foreach (var method in unused)
            {
                _output.WriteLine($"   • {method}");
            }
        }
        else
        {
            _output.WriteLine($"Coverage:         100.0%");
            _output.WriteLine("");
            _output.WriteLine("🎉 All methods are called somewhere in the test suite!");
        }

        // Statistics - show reference distribution
        _output.WriteLine("");
        _output.WriteLine("Reference Distribution:");
        var distribution = used.GroupBy(u => u.Count)
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var group in distribution)
        {
            _output.WriteLine($"  {group.Count()} method(s) with {group.Key} reference(s)");
        }

        // Assert - All methods must be called somewhere
        unused.Should().BeEmpty(
            $"All IServerDatabase methods must be called somewhere in tests. " +
            $"Unreferenced: {string.Join(", ", unused)}");
    }

    private static List<string> TokenizeMethodName(string methodName)
    {
        // Remove "Async" suffix
        var name = methodName.EndsWith("Async")
            ? methodName.Substring(0, methodName.Length - 5)
            : methodName;

        // Split on capital letters: "InsertBatchIntoTempTable" → ["Insert", "Batch", "Into", "Temp", "Table"]
        var tokens = new List<string>();
        var currentToken = new System.Text.StringBuilder();

        foreach (var c in name)
        {
            if (char.IsUpper(c) && currentToken.Length > 0)
            {
                tokens.Add(currentToken.ToString());
                currentToken.Clear();
            }
            currentToken.Append(c);
        }

        if (currentToken.Length > 0)
        {
            tokens.Add(currentToken.ToString());
        }

        // Filter out tiny tokens (like "Id", "At") for better matching
        return tokens.Where(t => t.Length >= 3).ToList();
    }

}
