// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace XUnitWrapperGenerator;

public interface ITestInfo
{
    string TestNameExpression { get; }
    string DisplayNameForFiltering { get; }
    string Method { get; }
    string ContainingType { get; }

    CodeBuilder GenerateTestExecution(ITestReporterWrapper testReporterWrapper);
}

public interface ITestReporterWrapper
{
    CodeBuilder WrapTestExecutionWithReporting(CodeBuilder testExecution, ITestInfo test);

    string GenerateSkippedTestReporting(ITestInfo skippedTest);
}

public sealed class BasicTestMethod : ITestInfo
{
    public BasicTestMethod(IMethodSymbol method,
                           string externAlias,
                           ImmutableArray<string> arguments = default,
                           string? displayNameExpression = null)
    {
        var args = arguments.IsDefaultOrEmpty ? "" : string.Join(", ", arguments);

        ContainingType = method.ContainingType.ToDisplayString(XUnitWrapperGenerator
                                                               .FullyQualifiedWithoutGlobalNamespace);
        Method = method.Name;
        DisplayNameForFiltering = $"{ContainingType}.{Method}({args})";

        // Make arguments interpolated expressions to avoid issues with string arguments.
        ImmutableArray<string> argumentsForName = arguments.IsDefaultOrEmpty
            ? ImmutableArray<string>.Empty
            : arguments.Select(arg => $"{{{arg}}}").ToImmutableArray();

        TestNameExpression = displayNameExpression ?? $"$\"{externAlias}::{ContainingType}.{Method}({string.Join(", ", argumentsForName)})\"";

        if (method.IsStatic)
        {
            _executionStatement = $"{externAlias}::{ContainingType}.{Method}({args});";
        }
        else
        {
            _executionStatement = $"using ({externAlias}::{ContainingType} obj = new()) obj.{Method}({args});";
        }
    }

    public string TestNameExpression { get; }
    public string DisplayNameForFiltering { get; }
    public string Method { get; }
    public string ContainingType { get; }

    private string _executionStatement { get; }

    public CodeBuilder GenerateTestExecution(ITestReporterWrapper testReporterWrapper)
    {
        return testReporterWrapper.WrapTestExecutionWithReporting(CodeBuilder.CreateNewLine(_executionStatement),
                                                                  this);
    }

    public override bool Equals(object obj)
    {
        return obj is BasicTestMethod other
            && TestNameExpression == other.TestNameExpression
            && Method == other.Method
            && ContainingType == other.ContainingType
            && _executionStatement == other._executionStatement;
    }

    public override int GetHashCode()
    {
        int hash = 17;
        hash = hash * 23 + (TestNameExpression?.GetHashCode() ?? 0);
        hash = hash * 23 + (Method?.GetHashCode() ?? 0);
        hash = hash * 23 + (ContainingType?.GetHashCode() ?? 0);
        hash = hash * 23 + (_executionStatement?.GetHashCode() ?? 0);
        return hash;
    }
}

public sealed class LegacyStandaloneEntryPointTestMethod : ITestInfo
{
    public LegacyStandaloneEntryPointTestMethod(IMethodSymbol method, string externAlias)
    {
        ContainingType = method.ContainingType.ToDisplayString(XUnitWrapperGenerator
                                                               .FullyQualifiedWithoutGlobalNamespace);
        Method = method.Name;
        TestNameExpression = $"\"{externAlias}::{ContainingType}.{Method}()\"";
        DisplayNameForFiltering = $"{ContainingType}.{Method}()";

        _executionStatement = $"Xunit.Assert.Equal(100, {externAlias}::{ContainingType}.{Method}());";
    }

    public string TestNameExpression { get; }
    public string DisplayNameForFiltering { get; }
    public string Method { get; }
    public string ContainingType { get; }

    private string _executionStatement { get; }

    public CodeBuilder GenerateTestExecution(ITestReporterWrapper testReporterWrapper)
    {
        return testReporterWrapper.WrapTestExecutionWithReporting(CodeBuilder.CreateNewLine(_executionStatement),
                                                                  this);
    }

    public override bool Equals(object obj)
    {
        return obj is LegacyStandaloneEntryPointTestMethod other
            && TestNameExpression == other.TestNameExpression
            && Method == other.Method
            && ContainingType == other.ContainingType; ;
    }

    public override int GetHashCode()
    {
        int hash = 17;
        hash = hash * 23 + (TestNameExpression?.GetHashCode() ?? 0);
        hash = hash * 23 + (Method?.GetHashCode() ?? 0);
        hash = hash * 23 + (ContainingType?.GetHashCode() ?? 0);
        return hash;
    }
}

public sealed class ConditionalTest : ITestInfo
{
    public ConditionalTest(ITestInfo innerTest, string condition)
    {
        TestNameExpression = innerTest.TestNameExpression;
        DisplayNameForFiltering = innerTest.DisplayNameForFiltering;
        Method = innerTest.Method;
        ContainingType = innerTest.ContainingType;

        _innerTest = innerTest;
        _condition = condition;
   }

    public ConditionalTest(ITestInfo innerTest, Xunit.TestPlatforms platform)
        : this(innerTest, GetPlatformConditionFromTestPlatform(platform))
    {
    }

    public string TestNameExpression { get; }
    public string DisplayNameForFiltering { get; }
    public string Method { get; }
    public string ContainingType { get; }

    private ITestInfo _innerTest;
    private string _condition;

    public CodeBuilder GenerateTestExecution(ITestReporterWrapper testReporterWrapper)
    {
        CodeBuilder builder = new();
        builder.AppendLine($"if ({_condition})");

        using (builder.NewBracesScope())
        {
            builder.Append(_innerTest.GenerateTestExecution(testReporterWrapper));
        }

        builder.AppendLine($"else");

        using (builder.NewBracesScope())
        {
            builder.AppendLine("string reason = string.Empty;");
            builder.AppendLine(testReporterWrapper.GenerateSkippedTestReporting(_innerTest));
        }
        return builder;
    }

    public override bool Equals(object obj)
    {
        return obj is ConditionalTest other
            && TestNameExpression == other.TestNameExpression
            && Method == other.Method
            && ContainingType == other.ContainingType
            && _condition == other._condition
            && _innerTest.Equals(other._innerTest);
    }

    public override int GetHashCode()
    {
        int hash = 17;
        hash = hash * 23 + (TestNameExpression?.GetHashCode() ?? 0);
        hash = hash * 23 + (Method?.GetHashCode() ?? 0);
        hash = hash * 23 + (ContainingType?.GetHashCode() ?? 0);
        hash = hash * 23 + (_condition?.GetHashCode() ?? 0);
        hash = hash * 23 + (_innerTest?.GetHashCode() ?? 0);
        return hash;
    }

    private static string GetPlatformConditionFromTestPlatform(Xunit.TestPlatforms platform)
    {
        List<string> platformCheckConditions = new();

        if (platform.HasFlag(Xunit.TestPlatforms.Windows))
        {
            platformCheckConditions.Add("global::System.OperatingSystem.IsWindows()");
        }
        if (platform.HasFlag(Xunit.TestPlatforms.Linux))
        {
            platformCheckConditions.Add("global::System.OperatingSystem.IsLinux()");
        }
        if (platform.HasFlag(Xunit.TestPlatforms.OSX))
        {
            platformCheckConditions.Add("global::System.OperatingSystem.IsMacOS()");
        }
        if (platform.HasFlag(Xunit.TestPlatforms.illumos))
        {
            platformCheckConditions.Add(@"global::System.OperatingSystem.IsOSPlatform(""illumos"")");
        }
        if (platform.HasFlag(Xunit.TestPlatforms.Solaris))
        {
            platformCheckConditions.Add(@"global::System.OperatingSystem.IsOSPlatform(""Solaris"")");
        }
        if (platform.HasFlag(Xunit.TestPlatforms.Android))
        {
            platformCheckConditions.Add("global::System.OperatingSystem.IsAndroid()");
        }
        if (platform.HasFlag(Xunit.TestPlatforms.iOS))
        {
            platformCheckConditions.Add("(global::System.OperatingSystem.IsIOS()"
                                      + " && !global::System.OperatingSystem.IsMacCatalyst())");
        }
        if (platform.HasFlag(Xunit.TestPlatforms.tvOS))
        {
            platformCheckConditions.Add("global::System.OperatingSystem.IsTvOS()");
        }
        if (platform.HasFlag(Xunit.TestPlatforms.MacCatalyst))
        {
            platformCheckConditions.Add("global::System.OperatingSystem.IsMacCatalyst()");
        }
        if (platform.HasFlag(Xunit.TestPlatforms.Browser))
        {
            platformCheckConditions.Add("global::System.OperatingSystem.IsBrowser()");
        }
        if (platform.HasFlag(Xunit.TestPlatforms.FreeBSD))
        {
            platformCheckConditions.Add(@"global::System.OperatingSystem.IsFreeBSD()");
        }
        if (platform.HasFlag(Xunit.TestPlatforms.NetBSD))
        {
            platformCheckConditions.Add(@"global::System.OperatingSystem.IsOSPlatform(""NetBSD"")");
        }

        return string.Join(" || ", platformCheckConditions);
    }
}

public sealed class MemberDataTest : ITestInfo
{
    public string TestNameExpression { get; }
    public string DisplayNameForFiltering { get; }
    public string Method { get; }
    public string ContainingType { get; }

    private ITestInfo _innerTest;
    private string _memberInvocation;
    private string _loopVarIdentifier;

    public MemberDataTest(ISymbol referencedMember,
                          ITestInfo innerTest,
                          string externAlias,
                          string argumentLoopVarIdentifier)
    {
        TestNameExpression = innerTest.TestNameExpression;
        Method = innerTest.Method;
        ContainingType = innerTest.ContainingType;
        DisplayNameForFiltering = $"{ContainingType}.{Method}(...)";

        _innerTest = innerTest;
        _loopVarIdentifier = argumentLoopVarIdentifier;

        string containingType = referencedMember.ContainingType.ToDisplayString(XUnitWrapperGenerator
                                                                                .FullyQualifiedWithoutGlobalNamespace);
        _memberInvocation = referencedMember switch
        {
            IPropertySymbol { IsStatic: true } => $"{externAlias}::{containingType}.{referencedMember.Name}",

            IMethodSymbol { IsStatic: true, Parameters.Length: 0 } =>
                $"{externAlias}::{containingType}.{referencedMember.Name}()",

            _ => throw new ArgumentException("MemberDataTest only supports properties and parameterless methods",
                                             nameof(referencedMember))
        };
    }

    public CodeBuilder GenerateTestExecution(ITestReporterWrapper testReporterWrapper)
    {
        CodeBuilder builder = new();

        builder.AppendLine();
        builder.AppendLine($@"foreach (object[] {_loopVarIdentifier} in {_memberInvocation})");

        using (builder.NewBracesScope())
        {
            builder.Append(_innerTest.GenerateTestExecution(testReporterWrapper));
        }
        return builder;
    }

    public override bool Equals(object obj)
    {
        return obj is MemberDataTest other
            && TestNameExpression == other.TestNameExpression
            && Method == other.Method
            && ContainingType == other.ContainingType
            && _innerTest.Equals(other._innerTest);
    }

    public override int GetHashCode()
    {
        int hash = 17;
        hash = hash * 23 + (TestNameExpression?.GetHashCode() ?? 0);
        hash = hash * 23 + (Method?.GetHashCode() ?? 0);
        hash = hash * 23 + (ContainingType?.GetHashCode() ?? 0);
        hash = hash * 23 + (_innerTest?.GetHashCode() ?? 0);
        return hash;
    }
}

public sealed class OutOfProcessTest : ITestInfo
{
    public string TestNameExpression { get; }
    public string DisplayNameForFiltering { get; }
    public string Method { get; }
    public string ContainingType => "OutOfProcessTest";

    private CodeBuilder _executionStatement { get; }
    private string RelativeAssemblyPath { get; }

    public OutOfProcessTest(string displayName, string relativeAssemblyPath, string? testBuildMode)
    {
        Method = displayName;
        DisplayNameForFiltering = displayName;
        TestNameExpression = $"@\"{displayName}\"";
        RelativeAssemblyPath = relativeAssemblyPath;

        // Native AOT tests get generated into a 'native' directory, so we need to get out of that one first to find the test
        string testPathPrefix = string.Equals(testBuildMode, "nativeaot", StringComparison.OrdinalIgnoreCase) ? "\"..\"" : "null";

        _executionStatement = new CodeBuilder();
        _executionStatement.AppendLine();
        _executionStatement.AppendLine("if (TestLibrary.OutOfProcessTest.OutOfProcessTestsSupported)");

        using (_executionStatement.NewBracesScope())
        {
            _executionStatement.AppendLine($@"TestLibrary.OutOfProcessTest"
                                        + $@".RunOutOfProcessTest(@""{relativeAssemblyPath}"", {testPathPrefix});");
        }
    }

    public CodeBuilder GenerateTestExecution(ITestReporterWrapper testReporterWrapper) =>
        testReporterWrapper.WrapTestExecutionWithReporting(_executionStatement, this);

    public override bool Equals(object obj)
    {
        return obj is OutOfProcessTest other
        && DisplayNameForFiltering == other.DisplayNameForFiltering
        && RelativeAssemblyPath == other.RelativeAssemblyPath;
    }

    public override int GetHashCode()
    {
        int hash = 17;
        hash = hash * 23 + (DisplayNameForFiltering?.GetHashCode() ?? 0);
        hash = hash * 23 + (RelativeAssemblyPath?.GetHashCode() ?? 0);
        return hash;
    }
}

public sealed class TestWithCustomDisplayName : ITestInfo
{
    public string DisplayNameForFiltering { get; }
    public string TestNameExpression => $@"""{DisplayNameForFiltering.Replace(@"\", @"\\")}""";
    public string Method => _inner.Method;
    public string ContainingType => _inner.ContainingType;

    private ITestInfo _inner;

    public TestWithCustomDisplayName(ITestInfo inner, string displayName)
    {
        _inner = inner;
        DisplayNameForFiltering = displayName;
    }

    public CodeBuilder GenerateTestExecution(ITestReporterWrapper testReporterWrapper)
    {
        ITestReporterWrapper dummyInnerWrapper = new NoTestReporting();
        CodeBuilder innerExecution = _inner.GenerateTestExecution(dummyInnerWrapper);
        return testReporterWrapper.WrapTestExecutionWithReporting(innerExecution, this);
    }

    public override bool Equals(object obj)
    {
        return obj is TestWithCustomDisplayName other
            && _inner.Equals(other._inner)
            && DisplayNameForFiltering == other.DisplayNameForFiltering;
    }

    public override int GetHashCode()
    {
        int hash = 17;
        hash = hash * 23 + (_inner?.GetHashCode() ?? 0);
        hash = hash * 23 + (DisplayNameForFiltering?.GetHashCode() ?? 0);
        return hash;
    }
}

public sealed class NoTestReporting : ITestReporterWrapper
{
    public CodeBuilder WrapTestExecutionWithReporting(CodeBuilder testExecution, ITestInfo test) => testExecution;

    public string GenerateSkippedTestReporting(ITestInfo skippedTest) => string.Empty;
}

public sealed class WrapperLibraryTestSummaryReporting : ITestReporterWrapper
{
    private readonly string _summaryLocalIdentifier;
    private readonly string _filterLocalIdentifier;
    private readonly string _outputRecorderIdentifier;

    public WrapperLibraryTestSummaryReporting(string summaryLocalIdentifier,
                                              string filterLocalIdentifier,
                                              string outputRecorderIdentifier)
    {
        _summaryLocalIdentifier = summaryLocalIdentifier;
        _filterLocalIdentifier = filterLocalIdentifier;
        _outputRecorderIdentifier = outputRecorderIdentifier;
    }

    public CodeBuilder WrapTestExecutionWithReporting(CodeBuilder testExecutionExpression,
                                                      ITestInfo test)
    {
        CodeBuilder builder = new();

        builder.AppendLine($"if ({_filterLocalIdentifier} is null"
                         + $" || {_filterLocalIdentifier}.ShouldRunTest(@\"{test.ContainingType}.{test.Method}\","
                         + $" {test.TestNameExpression}))");

        using (builder.NewBracesScope())
        {
            builder.AppendLine($"System.TimeSpan testStart = stopwatch.Elapsed;");
            builder.AppendLine("try");

            using (builder.NewBracesScope())
            {
                builder.AppendLine($"{_summaryLocalIdentifier}.ReportStartingTest("
                                 + $"{test.TestNameExpression},"
                                 + $" System.Console.Out);");

                builder.AppendLine($"{_outputRecorderIdentifier}.ResetTestOutput();");
                builder.Append(testExecutionExpression);

                builder.AppendLine($"{_summaryLocalIdentifier}.ReportPassedTest("
                                 + $"{test.TestNameExpression},"
                                 + $" \"{test.ContainingType}\","
                                 + $" @\"{test.Method}\","
                                 + $" stopwatch.Elapsed - testStart,"
                                 + $" {_outputRecorderIdentifier}.GetTestOutput(),"
                                 + $" System.Console.Out,"
                                 + $" tempLogSw,"
                                 + $" statsCsvSw);");
            }

            builder.AppendLine("catch (System.Exception ex)");

            using (builder.NewBracesScope())
            {
                builder.AppendLine($"{_summaryLocalIdentifier}.ReportFailedTest("
                                 + $"{test.TestNameExpression},"
                                 + $" \"{test.ContainingType}\","
                                 + $" @\"{test.Method}\","
                                 + $" stopwatch.Elapsed - testStart,"
                                 + $" ex,"
                                 + $" {_outputRecorderIdentifier}.GetTestOutput(),"
                                 + $" System.Console.Out,"
                                 + $" tempLogSw,"
                                 + $" statsCsvSw);");
            }
        }

        builder.AppendLine("else");

        using (builder.NewBracesScope())
        {
            builder.AppendLine($"string reason = {_filterLocalIdentifier}"
                             + $".GetTestExclusionReason({test.TestNameExpression});");
            builder.AppendLine(GenerateSkippedTestReporting(test));
        }
        return builder;
    }

    public string GenerateSkippedTestReporting(ITestInfo skippedTest)
    {
        return $"{_summaryLocalIdentifier}.ReportSkippedTest("
             + $"{skippedTest.TestNameExpression},"
             + $" \"{skippedTest.ContainingType}\","
             + $" @\"{skippedTest.Method}\","
             + $" System.TimeSpan.Zero,"
             + $" reason,"
             + $" tempLogSw,"
             + $" statsCsvSw);";
    }
}
