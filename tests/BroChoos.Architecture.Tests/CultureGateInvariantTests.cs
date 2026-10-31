using System.Xml.Linq;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace BroChoos.Architecture.Tests;

// F2: THE e + 1 RULE IN CultureGateState (tests/BroChoos.App.Tests/CultureGate.cs) RESTS ON HOW THE
// GATE IS TAKEN AND RELEASED. This parses the BroChoos.App.Tests sources with Roslyn and checks the
// syntax trees (comments and string literals are trivia or tokens, not calls, so they neither
// match nor hide one).
//
// WHAT IT PINS: the syntax checks listed in THE RULES and nothing more: the shape of every use of
// CultureGate.Gate and CultureGate.State, the control-flow shapes of
// CultureIsolatedAttribute.Before / TakeGate / After, that the attribute is sealed and declares
// nothing named TakeGate or CultureGate besides the TakeGate method, and that no scanned source
// names Before or After.
// WHAT IT DOES NOT PIN:
//   - Whether a statement the rules allow can throw. That covers the statements before State.Take
//     in TakeGate and the try block of After. A throw in TakeGate leaks the gate, because xUnit v2
//     (XunitTestInvoker 2.9.2) runs After only for attributes whose Before returned normally. The
//     rules ban only the throw, return and goto KEYWORDS there (throw statements and throw
//     expressions in TakeGate before State.Take, outside lambdas and local functions, and anywhere
//     in After), plus using declarations and using statements in Before and TakeGate and foreach
//     in TakeGate (rule 3); any expression they allow can still throw. The code keeps those
//     statements to field and AsyncLocal assignments and locks.
//   - That State.Release in After's finally cannot throw. If it did, Gate.Release after it would
//     be skipped and the gate would leak. State.Release only takes a lock and does two volatile
//     writes, so this is accepted rather than pinned by a nested try (rule 4). An over-release
//     (Gate.Release on a full semaphore) throws SemaphoreFullException from the finally, which
//     replaces any exception the try block was propagating.
//   - Binding. There is no semantic model, so names are matched by spelling (see HOW .. FOUND).
//   - Reflection, dynamic, code in referenced assemblies, generated obj\*.cs files, and MSBuild
//     files outside the list in SOURCES.
//   - What CultureGateState does inside its own methods (CultureGateStateTests covers that).
//
// SOURCES. Every .cs under the project (only the project-root bin and obj folders are excluded; a
// nested folder named bin or obj is scanned) plus every <Compile Include=..> item in the csproj
// and in each Directory.Build.props / Directory.Build.targets / Directory.Packages.props from the
// project folder up to the repository root. Elements and metadata are matched by local name,
// ignoring case as MSBuild does for item types and metadata; the Include / Update attribute names
// are matched exactly, as MSBuild does for attributes (see MsBuildName). An Include that holds
// $(, @(, %(, * , ? or ; cannot be resolved here and fails the scan, so does a missing file, any
// <Import> and any <Target> in those MSBuild files (a task in a target could create Using,
// Compile, InternalsVisibleTo or AssemblyAttribute items at build time). An MSBuild file can also
// be imported with no <Import> at all, through a property that feeds the path of an SDK <Import>
// (CustomAfterMicrosoftCommonTargets, DirectoryBuildTargetsPath, _DirectoryBuildTargetsFile, ..):
// setting any property in ImportProperties in those files fails the scan, and so does setting
// any property in TaskAssemblyProperties (they feed the AssemblyFile of an SDK <UsingTask>, so
// they pick the DLL that runs a task such as Csc). So does setting CompilerResponseFile,
// CscToolExe or CscToolPath (CompilerProperties), and a DefineConstants value that adds any
// symbol other than DEBUG or TRACE. SDK targets that compile repository files into obj\*.g.cs
// are kept off: setting a property in SwitchProperties (UseWPF, UseWindowsForms, UseMaui, the
// Razor and generator switches, the target framework / platform identity) fails, every
// <TargetFramework> / <TargetFrameworks> must equal PinnedTargetFramework, the project's Sdk
// attribute must be Microsoft.NET.Sdk (compared ignoring case, as MSBuild resolves SDK names, but
// with no version and no list of SDKs), and a <Sdk> element fails. Avalonia XAML is kept off as
// well: an <AvaloniaXaml> or <AvaloniaResource> item fails, and so does any *.axaml file under
// the project (only the project-root bin and obj folders are excluded), because Avalonia's
// XamlIl compiler and Avalonia.Generators turn them into code the scan cannot see (the default
// Avalonia globs pick up **\*.axaml with no item in the scanned files). A Directory.Build.rsp
// from the project folder up to the repository root fails too: MSBuild reads the one nearest
// the ENTRY project (the BroChoos.sln at the root in CI, or this project when it is built
// directly), and it can pass -p: for any of those properties. An <Analyzer Include=..> item, an
// item whose OutputItemType names Analyzer (<ProjectReference .. OutputItemType="Analyzer"/>), an
// <AddModules> item, a <FrameworkReference> item (a framework's ref pack can carry analyzers and
// source generators: Microsoft.AspNetCore.App.Ref ships Microsoft.AspNetCore.App.SourceGenerators),
// and a <Page> or <ApplicationDefinition> item (the TFM targets Windows, so the SDK imports
// Microsoft.WinFX.targets, whose MarkupCompilePass1 runs whenever @(Page) or
// @(ApplicationDefinition) is not empty and writes obj\*.g.cs) fail as well: a source generator
// could emit code the scan cannot see, and a netmodule becomes part of this assembly. Out of
// scope: analyzers and generators that come from packages (xunit.analyzers arrives that way,
// transitively), properties set on the command line and environment variables (MSBuild reads
// every one of them as a property), and the Generator metadata on <Content>, <None>,
// <EmbeddedResource> and the like: it names a Visual Studio single-file generator, which only
// the IDE runs; no .props / .targets file in SDK 10.0.201 reads %(Generator), so a command-line
// build (CI) never acts on it.
// The scanned set can be larger than the compiled one: <Compile Remove=..>, a Condition on an
// item group, or EnableDefaultCompileItems=false drop files from the build that the scan still
// reads. That only makes the scan stricter. With EnableDefaultCompileItems=false the compiled
// files are exactly the <Compile Include=..> items, and each of those is scanned as above.
// Every file is parsed twice, once per CI configuration: Debug (DEBUG;TRACE) and Release (TRACE),
// and every count below must hold in EACH.
//
// HOW CultureGate MEMBERS ARE FOUND. CultureGate is a static class, so its members are reachable
// as CultureGate.X (any qualifier ending in CultureGate), as a bare name inside class CultureGate,
// or through a using static / alias of it. Those are forbidden in source (using directives) and in
// MSBuild. A <Using> item lands in obj\GlobalUsings.g.cs, outside the scan, so in the scanned
// MSBuild files every <Using> item with an Alias (even an empty one), or with a Static other than
// a literal false (as an attribute or as child metadata, in any letter case), fails whatever it
// names, and so does every <Using> whose Include or Update contains CultureGate, CultureIsolated,
// InternalsVisibleTo (each also covers its ..Attribute / ..State spelling) or a computed value
// ($(, @(, %(). The repository has no <Using> item today. As a second line, a bare Gate or State
// outside class CultureGate fails unless a member, local, parameter or other name Gate / State is
// declared in its scope (syntax level: the enclosing types' members and anything declared inside
// the enclosing member).
//
// THE RULES. Exactly one class CultureGate and one class CultureIsolatedAttribute are declared, and
// class CultureGateState is declared only in CultureGate.cs, once, without the partial modifier
// (a second part elsewhere could add members or hide its shape from this scan).
//   1. CultureGate.Gate outside class CultureGate, and a bare Gate inside it, may only be the
//      receiver of .CurrentCount, of an invoked .Wait(..) or of an invoked .Release(..). Anything
//      else (WaitAsync, AvailableWaitHandle, a method group, ?., parentheses, a local, an argument)
//      is a violation.
//   2. Wait and Release are called only inside CultureIsolatedAttribute: exactly 1 Release and
//      exactly 2 Waits, both in the method Before: exactly 1 positive "if (Gate.Wait(..))" and
//      exactly 1 negated "if (!Gate.Wait(..))", each Wait the whole condition of its if.
//      The positive if's block is exactly "{ TakeGate(..); return; }" (it cannot fall through to
//      the second Wait), and the if is a direct statement of Before's body or nested only in the
//      then-blocks of if statements inside it (the poison check).
//      The negated if has no else and is a DIRECT statement of Before's body. Its block ends in
//      throw (a return would let After release a gate this test never took), and that throw is the
//      block's only exit: no return, goto, yield, try, and no break / continue that leaves it.
//      The statement after it is TakeGate(..), and that is the LAST statement of Before's body.
//      Outside those two pinned if blocks, Before's body has no return, goto, yield or try.
//      (Lambdas and local functions are never searched for exits: their exits leave only them.)
//      So Before ends only by throwing before any Wait succeeded, or by calling TakeGate as its
//      final act. Nothing runs in Before after the acquisition, which matters because xUnit v2
//      does not call After when Before throws (see WHAT IT DOES NOT PIN).
//   3. The attribute class carries the sealed modifier (a subclass could call base.Before and then
//      throw). No scanned source uses the name Before or After as a simple name at all (a call,
//      member access, ?., base.X, method group or nameof): only xUnit may call them, since a direct
//      call could take a gate nobody releases or release one someone else holds. This ban is by
//      spelling, on every receiver; no other App.Tests code names Before or After today (comments
//      and string literals aside, which are not names). Inside the attribute nothing but the one
//      TakeGate method is named TakeGate or CultureGate: no field, property, event, method, nested
//      type, local, parameter, pattern designation, foreach / catch / query variable or local
//      function (each would shadow the method or the class).
//      The attribute declares exactly one method Before, one TakeGate and one After, directly, with
//      no local function of those names, and none of them has a type parameter list. Before and
//      After carry the override modifier and not new (a new method hides the base, and xUnit
//      would call the empty virtual). None of the three is async or contains an await keyword
//      (await would hand control back to xUnit before the gate is taken or released).
//      CultureGate.State.Take(..) is the LAST statement of TakeGate's body, with no return, throw
//      statement, throw expression or goto in the statements before it (lambdas and local
//      functions declared there are not searched: their exits leave only them), so nothing in
//      TakeGate runs after the acquisition is recorded. Its argument list is exactly one bare
//      identifier naming a local declared directly in TakeGate's body (holder), and each pinned
//      TakeGate(..) call passes exactly Before's parameter (methodUnderTest) as a bare
//      identifier: arguments are evaluated after the Wait succeeded, so an expression there
//      (x ?? throw ..) could throw and leak the gate.
//      Outside lambdas and local functions, Before and TakeGate hold no using declaration
//      (using var d = ..;) and no using statement, and TakeGate holds no foreach (its enumerator's
//      implicit Dispose): their Dispose runs after the acquisition, and a throw from it would leak
//      the gate.
//      TakeGate is named exactly twice inside the attribute (calls, method groups, nameof and
//      generic names such as TakeGate<T> all count), and both are the pinned TakeGate(..)
//      statements of rule 2.
//   4. After's body is exactly one statement, "try { .. } finally { CultureGate.State.Release();
//      Gate.Release(); }": no catch clause, and the finally holds exactly those two statements in
//      that order (Gate.Release with no arguments, as a statement or "_ = .."). Outside lambdas and
//      local functions, After holds no other try and no return, goto, yield, throw statement or
//      throw expression. A throw from a call in the try block still reaches the finally.
//   5. CultureGate.State (or a bare State inside class CultureGate) is only ever the receiver of a
//      member access; Take and Release on it are always invoked, Take exactly once (in TakeGate)
//      and Release exactly once (in After). The member must be one that CultureGateState declares
//      (the non-private members parsed from class CultureGateState, see StateMembers); anything
//      else, such as ToString or GetType, is a violation. Calls on other receivers (a test's own
//      "state") are not matched. An extension method with an allowed name (BeginWait, say) would
//      pass that check and could run anything, and the receiver type cannot be trusted (this
//      object o, this T o, extension(object o) all bind to CultureGate.State), so extensions are
//      banned BY NAME: no method with a this parameter, and no member of a C# 14 extension(..)
//      block, may be named like a CultureGateState member, whatever its receiver type.
//      This also fails an unrelated extension helper that happens to share such a name (Take,
//      Release, Holder, Epoch, ..) on any type, even one that never touches CultureGate. That is
//      intentional: the syntax scan cannot tell the two apart, so it fails closed; rename the
//      helper. As well, no this parameter and no extension(..) receiver may have the type
//      CultureGateState.
//      Out of scope: extensions declared in referenced assemblies (packages, other projects).
//   6. Another assembly must not be able to reach CultureGate unseen, so InternalsVisibleTo is
//      banned in every form the scan can see: an [assembly: InternalsVisibleTo(..)] attribute (also
//      written ..Attribute or qualified), a using alias whose target's rightmost name is
//      InternalsVisibleTo or InternalsVisibleToAttribute, and in the scanned MSBuild files an
//      <InternalsVisibleTo> item and any <Using> item that names it or carries an Alias (see HOW
//      CultureGate MEMBERS ARE FOUND for the full <Using> ban). Every <AssemblyAttribute> item in
//      the scanned MSBuild files is banned outright, whatever its Include (it is emitted to
//      obj\*.AssemblyInfo.cs, which the scan does not read). Item names are matched ignoring case
//      (<internalsvisibleto>, <assemblyattribute>), and no <Target> may create such items at build
//      time (SOURCES). Out of scope: MSBuild files the scan does not list, such as NuGet
//      build\*.props / *.targets and obj\*.nuget.g.props.
//   7. The attribute is used exactly twice in the scanned sources, counting every attribute whose
//      rightmost name is CultureIsolated or CultureIsolatedAttribute (qualified forms included),
//      and each usage sits on a class that also carries [CollectionDefinition(..)]. A using alias
//      of the attribute is banned (it would hide a usage), in source and as an MSBuild <Using>
//      item (any Alias, or an Include / Update naming CultureIsolated). Stacked scopes (the
//      collection definition plus a test class, a method or a base class) make xUnit v2 run
//      Before twice for one test, and the gate is not re-entrant: the second Before waits the
//      full CultureGate.Timeout (10 minutes) on the gate the first one took.
// The scan fails closed: any syntax error, any #define / #undef, and any #if / #elif whose
// condition names a symbol other than DEBUG or TRACE is a violation. With only those two symbols
// allowed, the two parses above see every branch the CI builds compile, in every file, whether or
// not it mentions CultureGate (deciding "mentions" would itself depend on the active branch).
//
// RULE -> PROBE. Each probe is a temporary edit (run by hand). All 115 listed here (41 earlier
// ones, 18 prefixed a7-, 4 prefixed a8-, 5 prefixed a9-, 11 prefixed a10-, 6 prefixed a11-, 9
// prefixed a12-, 13 prefixed a13- and 8 prefixed a14-) were run against the current rules. Every
// probe must turn this test red, except the eight marked GREEN, which must stay green: a8-thr-lam
// (the lambda exemption), a12-defok, a12-anr, a12-out, a12-rspx, a13-tfok, a13-oitp and a14-sdkl
// (each pins that a rule is no wider than it says).
// Grouped in the order of SOURCES, HOW .. FOUND, THE RULES and the fail-closed paragraph.
//   sources             F5 (.cs in a nested bin), a7-imp (<Import>), a7-cmp (<Compile Include> of
//                       a missing file), a11-cmpl (<compile Include="ZzMissing.cs"/>), a10-tgt
//                       (<Target Name="Zz"/>), a11-cact (<CustomAfterMicrosoftCommonTargets>),
//                       a11-cacl (<customaftermicrosoftcommontargets>), a11-dbtp
//                       (<DirectoryBuildTargetsPath>), a12-dbtf (<_DirectoryBuildTargetsFile>
//                       Zz.targets in Directory.Build.props), a12-out (GREEN: <OutputType>Exe in
//                       Directory.Build.props), a11-rsp (Directory.Build.rsp in the project
//                       folder), a12-rsr (Directory.Build.rsp at the repository root), a12-rspx
//                       (GREEN: Zz.rsp in the project folder), a12-crf (<CompilerResponseFile>),
//                       a12-def (<DefineConstants>$(DefineConstants);ZZ), a12-defok (GREEN:
//                       <DefineConstants>$(DefineConstants);TRACE), a11-anl (<Analyzer
//                       Include="x.dll"/>), a12-anr (GREEN: <Analyzer Remove="x.dll"/>), a12-pra
//                       (<ProjectReference Include="x.csproj" OutputItemType="Analyzer"/>),
//                       a13-pram (<ProjectReference Include="x.csproj"><outputitemtype>Analyzer
//                       </outputitemtype></ProjectReference>), a13-oitp (GREEN:
//                       <OutputItemType>Analyzer in a PropertyGroup), a13-amod (<AddModules
//                       Include="x.netmodule"/>), a13-cte (<CscToolExe>x.exe), a13-ctp
//                       (<CscToolPath>c:\x), a13-defc (<DefineConstants Condition="'$(Configuration)'
//                       =='Debug'">$(DefineConstants);ZZ), a13-rta (<RoslynTasksAssembly>x.dll),
//                       a13-wpf (<UseWPF>true), a13-tf (<TargetFramework>net10.0), a13-tfok
//                       (GREEN: <TargetFramework>net10.0-windows10.0.19041.0), a13-sdk (Project
//                       Sdk="Microsoft.NET.Sdk.Razor"), a13-fwr (<FrameworkReference
//                       Include="Microsoft.AspNetCore.App"/>), a13-page (<Page Include="x.xaml"/>),
//                       a14-sdkl (GREEN: Project Sdk="microsoft.net.sdk"), a14-sdkv (Project
//                       Sdk="Microsoft.NET.Sdk/10.0.201"; the message shows the value), a14-avx
//                       (<AvaloniaXaml Include="x.axaml"/>), a14-avr (<AvaloniaResource
//                       Include="x.axaml"/>), a14-axf (an empty Zz.axaml in the project folder),
//                       a14-iwdt (<ImportWindowsDesktopTargets>true), a14-ixt
//                       (<ImportXamlTargets>true), a14-tfm (<TargetFramework>net10.0; the message
//                       names PinnedTargetFramework)
//   using static        a7-uss (using static BroChoos.App.Tests.CultureGate;), a7-usg (<Using
//                       Include="..CultureGate" Static="true">), a7-bare (bare Gate outside the
//                       class), a9-usm (<Using Include="System.Math" Static="true">), a10-ul
//                       (<using Include="..CultureGate" Static="true"/>), a10-stu (<Using
//                       Include="X" STATIC="true"/>), a10-sch (<Using Include="X"><static>true
//                       </static></Using>), a10-sce (<Using Include="X"><Static>true</Static>
//                       </Using>), a10-stt (<Using Include="X" Static="True"/>), a10-upd (<Using
//                       Update="System" Alias="S"/>), a10-ae (<Using Include="X" Alias=""/>)
//   declarations        nf3 (partial CultureGateState), nf3b (CultureGateState in another file)
//   1 Gate shapes       g1 (Gate.WaitAsync() in another file), g2 (Gate?.Wait(0)), g3
//                       (Gate.AvailableWaitHandle), g4 (Func<int, bool> f = Gate.Wait;), g5 (Gate
//                       passed as an argument)
//   2 Waits in Before   b1 (two positive Waits), nf1a (negated if under a condition), nf1b (Wait
//                       outside Before), F1 (negated block returns), l2 (positive block lacks
//                       return), m4 (return in Before), pa1 (statement after the final TakeGate),
//                       a7-else (else on the negated if), a7-yield (yield break in Before; the
//                       method is then an iterator, which the compiler rejects too, but the scan
//                       fails on the yield first)
//   3 attribute         a7-m1a (new CultureIsolatedAttribute().After(..) in another file), a7-m1b
//                       (sealed removed), a7-m1c (subclass calling base.Before), a7-m2a (local
//                       Action<MethodInfo> TakeGate in Before), a7-m2b (field CultureGate in the
//                       attribute), b2a / b2b (new Before / After), a7-ovr (public void After, no
//                       override), d1 (generic TakeGate<T>), b3 (async Before with await), pa2
//                       (statement after State.Take), a8-thr-top (throw expression before
//                       State.Take), a8-thr-lam (GREEN: a lambda holding the same throw before
//                       State.Take), f4a (TakeGate(methodUnderTest ?? throw ..)), f4b
//                       (State.Take(holder ?? throw ..)), f3a / f3b (using var at the top of
//                       Before / TakeGate), a7-l1 (foreach before State.Take), d2 (TakeGate<T>
//                       method group), F2 (extra TakeGate in After)
//   4 After             nf5 (return in After), b4 (throw before the releases), b4b (Gate.Release
//                       moved into the try), r4a (catch clause on After's try), r4b (the two
//                       finally statements swapped), a7-try2 (extra try in After)
//   5 State / ext.      F3 (State.ToString), m1a / m1b / m1c (extension named like a member),
//                       b (this CultureGateState), c (extension(CultureGateState))
//   6 IVT               F4 ([assembly: InternalsVisibleTo]), e (using alias of IVT), a7-ivt
//                       (<InternalsVisibleTo> item), a (<AssemblyAttribute> IVT), m2
//                       (<AssemblyAttribute @(..)>), a9-uivt (<Using
//                       Include="..InternalsVisibleToAttribute" Alias="Ivt">), a10-ivt
//                       (<internalsvisibleto Include="X"/>), a10-aa (<assemblyattribute
//                       Include="X"/>)
//   7 [CultureIsolated] a8-iso-cls ([CultureIsolated] on a test class), a8-iso-mth
//                       ([CultureIsolated] on a test method), a9-uiso (<Using
//                       Include="..CultureIsolatedAttribute" Alias="Iso">), a9-ali (using Iso =
//                       ..CultureIsolatedAttribute;), a9-cnt (one of the two real [CultureIsolated]
//                       usages removed), a10-ali (<Using Include="..CultureIsolatedAttribute"
//                       alias="Iso"/>)
//   fail closed         a7-def (#define), a7-ifu (#if UNKNOWN)
public sealed class CultureGateInvariantTests(ITestOutputHelper output)
{
	private const string GateType = "CultureGate";
	private const string StateType = "CultureGateState";
	private const string AttributeType = "CultureIsolatedAttribute";
	private const string IsolatedName = "CultureIsolated";
	private const string GateFile = "CultureGate.cs";
	private const string ProjectName = "BroChoos.App.Tests";

	// ci.yml builds and tests Release and Debug. The SDK defines TRACE in both, DEBUG only in Debug.
	private static readonly (string Name, string[] Symbols)[] Configurations = [("Debug", ["DEBUG", "TRACE"]), ("Release", ["TRACE"])];

	private static readonly string[] KnownSymbols = ["DEBUG", "TRACE"];

	private static readonly string[] MsBuildFileNames = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"];

	// A property, item or metadata reference: its value is computed by MSBuild, not written here.
	private static readonly string[] ComputedValue = ["$(", "@(", "%("];

	// A wildcard or a list separator: one Include would name several files.
	private static readonly string[] WildcardOrList = ["*", "?", ";"];

	private static readonly string[] UnresolvableInclude = [.. ComputedValue, .. WildcardOrList];

	// An MSBuild <Using> item whose Include or Update contains one of these (ordinal substring, so
	// CultureIsolatedAttribute and InternalsVisibleToAttribute are covered too) fails the scan.
	private static readonly string[] UsingBannedNames = [GateType, IsolatedName, "InternalsVisibleTo"];

	// The MSBuild element names the scan acts on, in their canonical spelling; an element matches one
	// ignoring case (see MsBuildName).
	private static readonly string[] ScannedElements = ["Import", "Target", "Analyzer", "AddModules", "FrameworkReference", "Page", "ApplicationDefinition", "AvaloniaXaml", "AvaloniaResource", "InternalsVisibleTo", "AssemblyAttribute", "Using", "Compile"];

	// The properties that feed the path of an SDK <Import>. Setting one in a scanned MSBuild file
	// could import an MSBuild file the scan never reads, with no <Import> of its own
	// (<_DirectoryBuildTargetsFile>Zz.targets</..> in Directory.Build.props makes the SDK import
	// Zz.targets). A property element matches one ignoring case. Ordinal order.
	// HOW THE LIST WAS BUILT, from .NET SDK 10.0.201: a transitive closure over these files:
	// Current\Microsoft.Common.props, every *.props / *.targets under Current\ (the ImportAfter
	// folders), Microsoft.Common.targets, Microsoft.Common.CurrentVersion.targets,
	// Microsoft.Common.CrossTargeting.targets, Microsoft.CSharp.targets,
	// Microsoft.CSharp.CurrentVersion.targets, Roslyn\Microsoft.CSharp.Core.targets, NuGet.props,
	// NuGet.targets and every *.props / *.targets under Sdks\Microsoft.NET.Sdk (86 files).
	// Seed: every $(X) in the Project attribute of an <Import> (56 names). Step: for each name in
	// the set, every $(Y) in the text of any <Name>..</Name> inside a <PropertyGroup> of those files
	// joins the set (BaseIntermediateOutputPath feeds MSBuildProjectExtensionsPath, which names the
	// obj\*.nuget.g.props / .targets imports; _DirectoryBuildTargetsFile feeds
	// DirectoryBuildTargetsPath). Repeated until nothing new was added: 87 names.
	// LEFT OUT: the 54 names read only in an <Import> Condition. Each is an on/off switch or an
	// identity value (OutputType, TargetFrameworkIdentifier, TargetFrameworkVersion,
	// EnableNETAnalyzers, Language, PublishAot, UseArtifactsOutput, ..) that only chooses among
	// files shipped with the SDK, so it cannot bring repository code into the build. The Import*
	// switches were judged one by one and left out too: ImportDirectoryBuildProps,
	// ImportDirectoryBuildTargets and ImportDirectoryPackagesProps only drop repository files the
	// scan reads anyway (the scan reads them whether or not they are imported);
	// ImportProjectExtensionProps / ..Targets only drop the obj\*.nuget.g.* files (out of scope,
	// rule 6); ImportByWildcard* / ImportUserLocationsByWildcard* default to true and only drop
	// the SDK's ImportBefore / ImportAfter files and the user-profile extension folders (outside the
	// repository, machine set-up); ImportNuGetBuildTasksPackTargetsFromSdk and
	// ImportFrameworkWinFXTargets only switch on files that ship with the SDK (the latter, on a
	// .NETCoreApp target, only skips Microsoft.WinFX.targets). ImportWindowsDesktopTargets and
	// ImportXamlTargets switch on code-generating targets and are in SwitchProperties. Reserved names in
	// the list (MSBuildProjectFile and the like) cannot be set at all; they stay so that the list
	// is the whole closure.
	private static readonly string[] ImportProperties =
	[
		"AfterMicrosoftNETSdkTargets", "AfterMicrosoftNetSdkProps",
		"AfterTargetFrameworkInferenceTargets", "AlternateCommonProps", "AppDesignerFolder",
		"ArtifactsPath", "ArtifactsProjectName", "BaseIntermediateOutputPath",
		"BeforeMicrosoftNETSdkTargets", "BeforeTargetFrameworkInferenceTargets",
		"CSharpCoreTargetsPath", "CSharpDesignTimeTargetsPath", "CSharpTargetsPath",
		"CodeAnalysisTargets", "CommonTargetsPath", "ComposeWorkingDir",
		"CustomAfterDirectoryBuildProps", "CustomAfterDirectoryBuildTargets",
		"CustomAfterMicrosoftCSharpTargets", "CustomAfterMicrosoftCommonCrossTargetingTargets",
		"CustomAfterMicrosoftCommonProps", "CustomAfterMicrosoftCommonTargets",
		"CustomBeforeDirectoryBuildProps", "CustomBeforeDirectoryBuildTargets",
		"CustomBeforeMicrosoftCSharpTargets", "CustomBeforeMicrosoftCommonCrossTargetingTargets",
		"CustomBeforeMicrosoftCommonProps", "CustomBeforeMicrosoftCommonTargets",
		"DirectoryBuildPropsPath", "DirectoryBuildTargetsPath", "DirectoryPackagesPropsPath",
		"FSharpDesignTimeTargetsPath", "FSharpOverridesTargetsShim", "FSharpPropsShim",
		"FSharpTargetsShim", "ILCompilerTargetsPath", "ILLinkTargetsPath", "LanguageTargets",
		"MSBuildExtensionsPath", "MSBuildExtensionsPath32", "MSBuildProjectDirectory",
		"MSBuildProjectExtensionsPath", "MSBuildProjectFile", "MSBuildProjectFullPath",
		"MSBuildProjectName", "MSBuildSDKsPath", "MSBuildThisFile", "MSBuildThisFileDirectory",
		"MSBuildToolsPath", "MSBuildToolsVersion", "MSBuildUserExtensionsPath",
		"MicrosoftNETBuildExtensionsTargets", "MsAppxPackageTargets", "MsTestToolsTargets",
		"NETCoreSdkBundledCliToolsProps", "NETCoreSdkBundledMSBuildInformationProps",
		"NETCoreSdkBundledVersionsProps", "NETCoreSdkVersion", "NuGetBuildTasksPackTargets",
		"NuGetPackageRoot", "NuGetPropsFile", "NuGetRestoreTargets",
		"ProjectExtensionsPathForSpecifiedProject", "PublishProfile", "PublishProfileFullPath",
		"PublishProfileName", "ReportingServicesTargets", "RoslynTargetsPath", "StorePackageName",
		"StorePackageVersion", "StorePackageVersionForFolderName", "StoreWorkerWorkingDir", "TEMP",
		"VSTestTargets", "VisualStudioVersion", "WebPublishProfileFile", "_ContainersTargetsDir",
		"_DirectoryBuildPropsBasePath", "_DirectoryBuildPropsFile",
		"_DirectoryBuildTargetsBasePath", "_DirectoryBuildTargetsFile",
		"_DirectoryPackagesPropsBasePath", "_DirectoryPackagesPropsFile",
		"_PublishProfileDesignerFolder", "_PublishProfileRootFolder", "_RandomFileName",
		"_SourceLinkSdkSubDir",
	];

	// Csc task parameters (Roslyn\Microsoft.CSharp.Core.targets, CoreCompile) that come from a
	// property and can bring code into the compilation that the scan does not see.
	// CompilerResponseFile is passed as ResponseFiles: that file can add /define:, /analyzer: or
	// sources from anywhere. CscToolExe / CscToolPath pick the compiler binary itself. Banned
	// outright (a property element matches one ignoring case); the repository sets none of them.
	// DefineConstants is checked by value instead (in Sources), and the item-based parameters are
	// handled as items: Analyzers (@(Analyzer), and any item whose OutputItemType names Analyzer),
	// AddModules (@(AddModules), a netmodule merged into this assembly).
	// Not banned, because they cannot add code: the Csc parameters AdditionalFiles
	// (@(AdditionalFiles)) and AnalyzerConfigFiles (@(EditorConfigFiles); the SDK adds each
	// discovered .editorconfig and each @(GlobalAnalyzerConfigFiles) .globalconfig to that item),
	// inputs that only analyzers read (analyzers are banned above, and those from packages are out
	// of scope); EmbeddedFiles (@(EmbeddedFiles), copied into the PDB, not compiled);
	// AdditionalLibPaths ($(AdditionalLibPaths), search folders for references, which come only
	// from the reference items the SDK resolves; a reference is not source, see WHAT IT DOES NOT
	// PIN).
	private static readonly string[] CompilerProperties = ["CompilerResponseFile", "CscToolExe", "CscToolPath"];

	// The properties that feed the AssemblyFile / AssemblyName / TaskFactory of an SDK <UsingTask>:
	// they pick the DLL that runs a task, Csc included (RoslynTasksAssembly), so setting one could
	// run a compiler task the scan never sees. Built like ImportProperties, from the same 86 files:
	// seed every $(X) in those three attributes of every <UsingTask> (5 names:
	// DotNetApiCompatTaskAssembly, MicrosoftNETBuildTasksAssembly, RestoreTaskAssemblyFile,
	// RoslynTasksAssembly, VSTestTaskAssemblyFile), then the same <PropertyGroup> closure. These
	// are the 8 names it adds to ImportProperties' 87; ImportProperties itself is unchanged.
	// Ordinal order; a property element matches one ignoring case.
	private static readonly string[] TaskAssemblyProperties =
	[
		"DotNetApiCompatTaskAssembly", "MicrosoftNETBuildTasksAssembly", "MicrosoftNETBuildTasksDirectory",
		"MicrosoftNETBuildTasksDirectoryRoot", "MicrosoftNETBuildTasksTFM", "RestoreTaskAssemblyFile",
		"RoslynTasksAssembly", "VSTestTaskAssemblyFile",
	];

	// Switches that turn on SDK targets which compile repository files into obj\*.g.cs, a folder
	// the scan does not read: UseWPF / UseWindowsForms (Sdks\Microsoft.NET.Sdk.WindowsDesktop:
	// XAML Page compilation, x:Code; WPF globbing of **\*.xaml into @(Page)), EnableDefaultPageItems /
	// EnableDefaultApplicationDefinition (the WindowsDesktop Page / ApplicationDefinition globs),
	// IncludePackageReferencesDuringMarkupCompilation (the WinFX temporary-assembly pass),
	// UseMaui, the Razor switches (UsingMicrosoftNETSdkRazor imports the Razor targets from
	// Microsoft.NET.Sdk.BeforeCommon.targets; UseRazorSourceGenerator; UsingMicrosoftNETSdkWeb and
	// UsingMicrosoftNETSdkBlazorWebAssembly are set by those SDKs), and the target identity that
	// PinnedTargetFramework would otherwise not cover (TargetFrameworkIdentifier / ..Version /
	// ..Moniker, TargetPlatformIdentifier / ..Version: they decide which SDK targets are
	// imported), and two Import*Targets switches from SDK 10.0.201: ImportWindowsDesktopTargets
	// (true appends Microsoft.NET.Sdk.WindowsDesktop.targets to AfterMicrosoftNETSdkTargets in
	// Microsoft.NET.Sdk.targets, and that file imports Microsoft.WinFX.targets, XAML Page
	// compilation, even without UseWPF) and ImportXamlTargets (Microsoft.Common.CurrentVersion.targets
	// imports $(MSBuildToolsPath)\Microsoft.Xaml.targets, the XAML build-task targets; SDK 10.0.201
	// ships no such file, but Visual Studio's MSBuild does, so it is banned too). Neither is in
	// ImportProperties. Banned outright in a <PropertyGroup> (matched ignoring case); the repository
	// sets none of them in the scanned files (App.Tests uses Avalonia, not WPF).
	private static readonly string[] SwitchProperties =
	[
		"EnableDefaultApplicationDefinition", "EnableDefaultPageItems", "ImportWindowsDesktopTargets",
		"ImportXamlTargets", "IncludePackageReferencesDuringMarkupCompilation", "TargetFrameworkIdentifier",
		"TargetFrameworkMoniker", "TargetFrameworkVersion", "TargetPlatformIdentifier",
		"TargetPlatformVersion", "UseMaui", "UseRazorSourceGenerator", "UseWPF", "UseWindowsForms",
		"UsingMicrosoftNETSdkBlazorWebAssembly", "UsingMicrosoftNETSdkRazor", "UsingMicrosoftNETSdkWeb",
	];

	// The one target framework the CI builds App.Tests for (BroChoos.App.Tests.csproj). Any
	// <TargetFramework> or <TargetFrameworks> in the scanned files must be exactly this, so a
	// different TF cannot select other SDK targets. A TF upgrade changes three places together:
	// BroChoos.App.Tests.csproj, BroChoos.App.csproj (the two must match) and this constant.
	private const string PinnedTargetFramework = "net10.0-windows10.0.19041.0";

	// The only Sdk the project may name (Microsoft.NET.Sdk.Razor, .Web or .WindowsDesktop would
	// import code-generating targets). Compared ignoring case, as MSBuild resolves SDK names; a
	// version (Name/1.0) or a list (A;B) is a different value and fails.
	private const string PinnedSdk = "Microsoft.NET.Sdk";

	[Fact]
	public void CultureGate_IsTakenAndReleasedOnlyOnThePinnedPaths()
	{
		string root = RepositoryRoot();
		List<string> violations = [];
		List<(string Path, string Text)> sources = [.. Sources(root, violations).Select(path => (Path.GetRelativePath(root, path), File.ReadAllText(path)))];

		_ = sources.Should().NotBeEmpty("the scan must see the App.Tests sources, or it proves nothing");
		_ = sources.Select(source => Path.GetFileName(source.Path)).Should().Contain(GateFile);

		foreach ((string name, string[] symbols) in Configurations)
		{
			CSharpParseOptions options = new(LanguageVersion.Latest, DocumentationMode.Parse, SourceCodeKind.Regular, symbols);
			List<SyntaxTree> trees = [.. sources.Select(source => CSharpSyntaxTree.ParseText(source.Text, options, source.Path))];

			Scan scan = new(name);
			StateMembersFrom(trees, scan);
			foreach (SyntaxTree tree in trees)
			{
				Analyze(tree, scan);
			}

			CheckDeclarations(trees, scan);
			scan.ExpectCount("Gate.Wait in " + AttributeType, scan.Waits, 2);
			scan.ExpectCount("positive if (Gate.Wait(..)) in Before", scan.PositiveWaits, 1);
			scan.ExpectCount("negated if (!Gate.Wait(..)) in Before", scan.NegatedWaits, 1);
			scan.ExpectCount("Gate.Release in " + AttributeType, scan.Releases, 1);
			scan.ExpectCount("CultureGate.State.Take in TakeGate", scan.Takes, 1);
			scan.ExpectCount("CultureGate.State.Release in After", scan.StateReleases, 1);
			scan.ExpectCount($"[{IsolatedName}] usages (one per collection definition)", scan.IsolatedUses, 2);
			output.WriteLine($"{name}: stateMembers={scan.StateMembers.Count} ({string.Join(", ", scan.StateMembers.Order(StringComparer.Ordinal))})");
			output.WriteLine($"{name}: files={trees.Count} waits={scan.Waits} releases={scan.Releases} takes={scan.Takes} stateReleases={scan.StateReleases} takeGateUses={scan.TakeGateUses} isolatedUses={scan.IsolatedUses} violations={scan.Violations.Count}");
			violations.AddRange(scan.Violations);
		}

		_ = violations.Should().BeEmpty("every acquisition must record itself in CultureGateState right after Gate.Wait, and the holder must be cleared right before Gate.Release (the e + 1 rule depends on it)");
	}

	private static void Analyze(SyntaxTree tree, Scan scan)
	{
		SyntaxNode root = tree.GetRoot();
		foreach (Diagnostic error in tree.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
		{
			scan.Add(tree.FilePath, error.Location, $"syntax error {error.Id}");
		}

		foreach (SyntaxTrivia trivia in root.DescendantTrivia())
		{
			CheckDirective(tree.FilePath, trivia, scan);
		}

		foreach (UsingDirectiveSyntax usingDirective in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
		{
			if ((usingDirective.StaticKeyword != default || usingDirective.Alias is not null) && RightmostName(usingDirective.NamespaceOrType) == GateType)
			{
				scan.Add(tree.FilePath, usingDirective.GetLocation(), $"using static / alias of {GateType}");
			}

			// An alias of CultureGateState would hide an extension receiver from the rule 5 check.
			if (usingDirective.Alias is not null && RightmostName(usingDirective.NamespaceOrType) == StateType)
			{
				scan.Add(tree.FilePath, usingDirective.GetLocation(), $"using alias of {StateType}");
			}

			// An alias would hide the attribute's name from the check below ([assembly: IVT("X")]).
			if (usingDirective.Alias is not null && RightmostName(usingDirective.NamespaceOrType) is "InternalsVisibleTo" or "InternalsVisibleToAttribute")
			{
				scan.Add(tree.FilePath, usingDirective.GetLocation(), $"using alias of InternalsVisibleTo (it would hide an InternalsVisibleTo attribute from this scan): {usingDirective}");
			}

			// An alias would hide a [CultureIsolated] usage from the rule 7 count.
			if (usingDirective.Alias is not null && RightmostName(usingDirective.NamespaceOrType) is IsolatedName or AttributeType)
			{
				scan.Add(tree.FilePath, usingDirective.GetLocation(), $"using alias of {AttributeType} (it would hide a [{IsolatedName}] usage from this scan): {usingDirective}");
			}
		}

		foreach (AttributeSyntax attribute in root.DescendantNodes().OfType<AttributeSyntax>().Where(attribute => RightmostName(attribute.Name) is "InternalsVisibleTo" or "InternalsVisibleToAttribute"))
		{
			scan.Add(tree.FilePath, attribute.GetLocation(), $"InternalsVisibleTo would let another assembly reach {GateType} outside this scan: {attribute}");
		}

		// Rule 7 (M-A): xUnit v2 runs Before once per scope that carries the attribute (collection
		// definition, class, base class, method), and the second Before blocks on the gate the first
		// one took for the full Timeout. So every usage must sit on a [CollectionDefinition] class.
		foreach (AttributeSyntax isolated in root.DescendantNodes().OfType<AttributeSyntax>().Where(attribute => RightmostName(attribute.Name) is IsolatedName or AttributeType))
		{
			scan.IsolatedUses++;
			if (isolated.Parent?.Parent is not ClassDeclarationSyntax owner
				|| !owner.AttributeLists.SelectMany(list => list.Attributes).Any(attribute => RightmostName(attribute.Name) is "CollectionDefinition" or "CollectionDefinitionAttribute"))
			{
				scan.Add(tree.FilePath, isolated.GetLocation(), $"[{IsolatedName}] is not on a class that also carries [CollectionDefinition(..)] (on a test class, method or base class inside such a collection xUnit runs Before twice, and the second one blocks on the gate for {GateType}.Timeout): {Context(isolated)}");
			}
		}

		// Rule 5, by name: an extension with a CultureGateState member name binds to CultureGate.State
		// whatever its receiver type (object, an unconstrained T, ..), so the name alone is banned.
		foreach (MethodDeclarationSyntax method in root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(method => method.ParameterList.Parameters.Any(parameter => parameter.Modifiers.Any(SyntaxKind.ThisKeyword)) && scan.StateMembers.Contains(method.Identifier.ValueText)))
		{
			scan.Add(tree.FilePath, method.Identifier.GetLocation(), $"extension method named like a {StateType} member ({method.Identifier.ValueText}); whatever its receiver type it would bind to CultureGate.State and could run anything: {method.Identifier}{method.ParameterList}");
		}

		// Rule 5 for C# 14 extension blocks: by member name, and by receiver type (none may target
		// CultureGateState at all, whatever the member names).
		foreach (ExtensionBlockDeclarationSyntax block in root.DescendantNodes().OfType<ExtensionBlockDeclarationSyntax>())
		{
			foreach (string member in block.Members.SelectMany(MemberNames).Where(scan.StateMembers.Contains))
			{
				scan.Add(tree.FilePath, block.GetLocation(), $"extension block member named like a {StateType} member ({member}); whatever its receiver type it would bind to CultureGate.State and could run anything: {block.Keyword}{block.ParameterList}");
			}

			if (block.ParameterList?.Parameters.Any(parameter => IsStateType(parameter.Type)) ?? false)
			{
				scan.Add(tree.FilePath, block.GetLocation(), $"extension block on {StateType} (an allowed name could then run anything on CultureGate.State): {block.Keyword}{block.ParameterList}");
			}
		}

		// Rule 5, by type: no extension method may target CultureGateState at all, whatever its name.
		foreach (ParameterSyntax parameter in root.DescendantNodes().OfType<ParameterSyntax>().Where(parameter => parameter.Modifiers.Any(SyntaxKind.ThisKeyword) && IsStateType(parameter.Type)))
		{
			scan.Add(tree.FilePath, parameter.GetLocation(), $"extension method on {StateType} (an allowed name could then run anything on CultureGate.State): {Context(parameter)}");
		}

		foreach (IdentifierNameSyntax name in root.DescendantNodes().OfType<IdentifierNameSyntax>().Where(name => name.Identifier.ValueText is "Gate" or "State"))
		{
			if (IsBare(name) && !InsideType(name, GateType) && !IsDeclaredInScope(name))
			{
				scan.Add(tree.FilePath, name.GetLocation(), $"bare {name.Identifier.ValueText} outside {GateType} with nothing of that name declared in scope (a using static of {GateType}?): {Context(name)}");
			}
		}

		// M-1: Before and After are public, so any call (new CultureIsolatedAttribute().After(m),
		// a stray .Before(m), base.Before in a subclass) could release a gate someone else holds or
		// take one nobody releases. Their declarations are tokens, not names, so every simple name
		// Before or After (call, member access, ?., method group, nameof) is a violation.
		foreach (SimpleNameSyntax use in root.DescendantNodes().OfType<SimpleNameSyntax>().Where(name => name.Identifier.ValueText is "Before" or "After"))
		{
			scan.Add(tree.FilePath, use.GetLocation(), $"the name {use.Identifier.ValueText} is used (only xUnit may call {AttributeType}.Before / After; a direct call could take a gate nobody releases or release one someone else holds): {Context(use)}");
		}

		foreach (ExpressionSyntax gate in References(root, "Gate"))
		{
			CheckGateReference(tree.FilePath, gate, scan);
		}

		foreach (ExpressionSyntax state in References(root, "State"))
		{
			CheckStateReference(tree.FilePath, state, scan);
		}
	}

	// The names CultureGate.State may be followed by: the non-private members of class
	// CultureGateState as parsed from CultureGate.cs in this very configuration, nested types
	// excluded (they are not reachable through the instance). Parsed rather than hand-written, so a
	// member added to the class is in the set at once and the extension ban covers its name too. A
	// member with no accessibility modifier is private in C#, so only public, internal and protected
	// members count. If the class is not found exactly once, the scan fails.
	private static void StateMembersFrom(List<SyntaxTree> trees, Scan scan)
	{
		List<ClassDeclarationSyntax> classes = [.. trees.Where(tree => Path.GetFileName(tree.FilePath) == GateFile)
			.SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
			.Where(type => type.Identifier.ValueText == StateType)];
		scan.ExpectCount($"class {StateType} in {GateFile}", classes.Count, 1);
		foreach (MemberDeclarationSyntax member in classes.SelectMany(type => type.Members)
			.Where(member => member is not (BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
				&& member.Modifiers.Any(modifier => modifier.Kind() is SyntaxKind.PublicKeyword or SyntaxKind.InternalKeyword or SyntaxKind.ProtectedKeyword)))
		{
			scan.StateMembers.UnionWith(MemberNames(member));
		}

		if (scan.StateMembers.Count == 0)
		{
			scan.Violations.Add($"[{scan.Configuration}] no non-private member of {StateType} found in {GateFile}; the scan cannot tell which CultureGate.State members exist");
		}
	}

	private static void CheckDirective(string path, SyntaxTrivia trivia, Scan scan)
	{
		switch (trivia.GetStructure())
		{
			case DefineDirectiveTriviaSyntax or UndefDirectiveTriviaSyntax:
				scan.Add(path, trivia.GetLocation(), "#define / #undef (the scan parses only the CI symbol sets)");
				break;
			case ConditionalDirectiveTriviaSyntax conditional:
				foreach (IdentifierNameSyntax symbol in conditional.Condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Where(symbol => !KnownSymbols.Contains(symbol.Identifier.ValueText)))
				{
					scan.Add(path, trivia.GetLocation(), $"conditional directive on symbol {symbol.Identifier.ValueText} (only DEBUG and TRACE are evaluated)");
				}

				break;
			default:
				break;
		}
	}

	private static void CheckDeclarations(List<SyntaxTree> trees, Scan scan)
	{
		List<BaseTypeDeclarationSyntax> types = [.. trees.SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())];
		scan.ExpectCount("class " + GateType, types.Count(type => type.Identifier.ValueText == GateType && type is ClassDeclarationSyntax), 1);
		scan.ExpectCount("type named " + GateType, types.Count(type => type.Identifier.ValueText == GateType), 1);
		scan.ExpectCount("class " + AttributeType, types.Count(type => type.Identifier.ValueText == AttributeType && type is ClassDeclarationSyntax), 1);
		scan.ExpectCount("type named " + AttributeType, types.Count(type => type.Identifier.ValueText == AttributeType), 1);

		// CultureGateState lives in CultureGate.cs only, in one part: a partial part or a type of
		// that name elsewhere could add members the StateMembers parse never sees.
		foreach (BaseTypeDeclarationSyntax state in types.Where(type => type.Identifier.ValueText == StateType))
		{
			if (Path.GetFileName(state.SyntaxTree.FilePath) != GateFile)
			{
				scan.Add(state.SyntaxTree.FilePath, state.Identifier.GetLocation(), $"type {StateType} declared outside {GateFile}");
			}

			if (state.Modifiers.Any(SyntaxKind.PartialKeyword))
			{
				scan.Add(state.SyntaxTree.FilePath, state.Identifier.GetLocation(), $"{StateType} must not be partial (another part could add members this scan does not parse)");
			}
		}

		foreach (TypeDeclarationSyntax attribute in types.OfType<TypeDeclarationSyntax>().Where(type => type.Identifier.ValueText == AttributeType))
		{
			string path = attribute.SyntaxTree.FilePath;

			// M-1: a subclass could call base.Before and then throw, so After would never run.
			if (!attribute.Modifiers.Any(SyntaxKind.SealedKeyword))
			{
				scan.Add(path, attribute.Identifier.GetLocation(), $"{AttributeType} must be sealed (a subclass could call base.Before and then throw, which leaks the gate because xUnit then skips After): {attribute.Modifiers} class {attribute.Identifier}");
			}

			// M-2: a local, parameter, designation, field, property or other member named TakeGate or
			// CultureGate would shadow the method or the class, so a pinned TakeGate(..) call or a
			// CultureGate.X access could bind to it. Only the one TakeGate method itself is allowed.
			foreach (SyntaxNode node in attribute.DescendantNodes())
			{
				string[] names = node switch
				{
					MethodDeclarationSyntax { Identifier.ValueText: "TakeGate" } method when method.Parent == attribute => [],
					MemberDeclarationSyntax member => [.. MemberNames(member)],
					_ when LocalName(node) is { } local => [local],
					_ => [],
				};

				foreach (string shadow in names.Where(name => name is "TakeGate" or GateType))
				{
					scan.Add(path, node.GetLocation(), $"{AttributeType} declares something named {shadow} other than the TakeGate method (it would shadow the method or the class {GateType}): {Context(node)}");
				}
			}

			foreach (string method in (string[])["Before", "TakeGate", "After"])
			{
				List<MethodDeclarationSyntax> methods = [.. attribute.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(declaration => declaration.Identifier.ValueText == method)];
				if (methods.Count != 1 || methods[0].Parent != attribute)
				{
					scan.Add(path, attribute.Identifier.GetLocation(), $"{AttributeType} must declare exactly one method {method}, directly (found {methods.Count})");
				}

				foreach (MethodDeclarationSyntax generic in methods.Where(declaration => declaration.TypeParameterList is not null))
				{
					scan.Add(path, generic.Identifier.GetLocation(), $"{AttributeType}.{method} must not be generic (a generic name such as {method}<T> would escape the pinned call shapes): {generic.Identifier}{generic.TypeParameterList}");
				}

				foreach (LocalFunctionStatementSyntax local in attribute.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Where(local => local.Identifier.ValueText == method))
				{
					scan.Add(path, local.GetLocation(), $"local function {method} inside {AttributeType}");
				}

				foreach (MethodDeclarationSyntax declaration in methods)
				{
					// B2: "new void Before" hides the base and xUnit calls the empty virtual instead.
					if (method != "TakeGate" && (!declaration.Modifiers.Any(SyntaxKind.OverrideKeyword) || declaration.Modifiers.Any(SyntaxKind.NewKeyword)))
					{
						scan.Add(path, declaration.Identifier.GetLocation(), $"{AttributeType}.{method} must be declared override and not new (otherwise xUnit calls BeforeAfterTestAttribute's empty {method}): {declaration.Modifiers} {declaration.Identifier}");
					}

					// B3: an await hands control back to xUnit before the gate is taken or released.
					if (declaration.Modifiers.Any(SyntaxKind.AsyncKeyword) || declaration.DescendantNodes().OfType<AwaitExpressionSyntax>().Any())
					{
						scan.Add(path, declaration.Identifier.GetLocation(), $"{AttributeType}.{method} must not be async or contain await (xUnit would go on before it finished): {declaration.Modifiers} {declaration.Identifier}");
					}

					// F-3: a using declaration or statement disposes after the acquisition; if Dispose
					// throws, Before throws and xUnit skips After, so the gate leaks.
					if (method == "After")
					{
						continue;
					}

					foreach (SyntaxNode disposal in declaration.DescendantNodes(descendIntoChildren: node => node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
						.Where(node => node is UsingStatementSyntax || (node is LocalDeclarationStatementSyntax local && local.UsingKeyword.IsKind(SyntaxKind.UsingKeyword))))
					{
						scan.Add(path, disposal.GetLocation(), $"{AttributeType}.{method} has a using declaration or using statement (its Dispose runs after the acquisition, and a throw from it would leak the gate because xUnit then skips After): {Context(disposal)}");
					}

					// L-1: a foreach disposes its enumerator implicitly, and in TakeGate that Dispose runs
					// after the Wait succeeded.
					if (method != "TakeGate")
					{
						continue;
					}

					foreach (CommonForEachStatementSyntax loop in declaration.DescendantNodes(descendIntoChildren: node => node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)).OfType<CommonForEachStatementSyntax>())
					{
						scan.Add(path, loop.GetLocation(), $"{AttributeType}.TakeGate has a foreach (its implicit Dispose runs after the acquisition, and a throw from it would leak the gate because xUnit then skips After): {Context(loop)}");
					}
				}
			}

			// Before may end only by taking the gate or by throwing: no return, goto, yield or try
			// outside the two pinned if blocks (checked in CheckWait) and outside lambdas and local
			// functions. Otherwise After could release a gate Before never took.
			foreach (MethodDeclarationSyntax before in attribute.Members.OfType<MethodDeclarationSyntax>().Where(declaration => declaration.Identifier.ValueText == "Before"))
			{
				foreach (SyntaxNode exit in Exits(before, skip: node => scan.PinnedIfs.Any(pinned => pinned.Statement == node), throws: false))
				{
					scan.Add(path, exit.GetLocation(), $"{AttributeType}.Before has a return, goto, yield or try outside the two pinned if (Gate.Wait..) blocks (After could then release a gate Before never took): {Context(exit)}");
				}
			}

			// After must reach both releases on every path: its body is exactly the pinned
			// try { .. } finally { State.Release(); Gate.Release(); }, and outside lambdas and local
			// functions it holds no other try and no return, goto, yield or throw.
			foreach (MethodDeclarationSyntax after in attribute.Members.OfType<MethodDeclarationSyntax>().Where(declaration => declaration.Identifier.ValueText == "After"))
			{
				TryStatementSyntax? pinned = PinnedAfterTry(after);
				if (pinned is null)
				{
					scan.Add(path, after.Identifier.GetLocation(), $"the body of {AttributeType}.After is not exactly try {{ .. }} finally {{ CultureGate.State.Release(); CultureGate.Gate.Release(); }} with no catch: {after.Body?.ToString().ReplaceLineEndings(" ") ?? "no block body"}");
				}

				foreach (SyntaxNode exit in Exits(after, skip: _ => false, throws: true).Where(exit => exit != pinned))
				{
					scan.Add(path, exit.GetLocation(), $"{AttributeType}.After has a return, goto, yield, throw or extra try (it could leave before releasing the gate): {Context(exit)}");
				}
			}

			// Every use of the name TakeGate in the attribute (call, method group, nameof) must be one
			// of the two pinned TakeGate(..) statements found after the Waits.
			// Generic names (TakeGate<T>) count too: they are never one of the pinned shapes.
			List<SimpleNameSyntax> takeGates = [.. attribute.DescendantNodes().OfType<SimpleNameSyntax>().Where(name => name is IdentifierNameSyntax or GenericNameSyntax && name.Identifier.ValueText == "TakeGate")];
			scan.TakeGateUses += takeGates.Count;
			foreach (SimpleNameSyntax use in takeGates.Where(use => !scan.PinnedTakeGates.Any(pinned => pinned is InvocationExpressionSyntax call && call.Expression == use)))
			{
				scan.Add(path, use.GetLocation(), $"TakeGate used outside the two pinned positions after the Waits: {Context(use)}");
			}

			// F-4: the pinned TakeGate(..) arguments are evaluated after the Wait succeeded, so each must
			// be exactly Before's own parameter, as a bare identifier (nothing that can throw).
			string? parameter = attribute.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(declaration => declaration.Identifier.ValueText == "Before")?.ParameterList.Parameters is [var only] ? only.Identifier.ValueText : null;
			foreach (InvocationExpressionSyntax call in scan.PinnedTakeGates.OfType<InvocationExpressionSyntax>().Where(call => parameter is null || !IsSingleBareIdentifier(call.ArgumentList, name => name == parameter)))
			{
				scan.Add(path, call.GetLocation(), $"the argument list of a pinned TakeGate(..) is not exactly Before's parameter ({parameter ?? "Before has not exactly one parameter"}) as a bare identifier (it is evaluated after the Wait succeeded, and a throw there would leak the gate): {Context(call)}");
			}
		}

		scan.ExpectCount("uses of TakeGate in " + AttributeType, scan.TakeGateUses, 2);
	}

	// Every expression that denotes CultureGate.<member>: a member access whose receiver ends in
	// CultureGate, or the bare name inside class CultureGate. A qualified NAME (type syntax) that
	// ends in CultureGate.<member> is returned too, and is then rejected as an unknown shape.
	private static IEnumerable<ExpressionSyntax> References(SyntaxNode root, string member)
	{
		foreach (IdentifierNameSyntax name in root.DescendantNodes().OfType<IdentifierNameSyntax>().Where(name => name.Identifier.ValueText == member))
		{
			ExpressionSyntax? reference = name.Parent switch
			{
				MemberAccessExpressionSyntax access when access.Name == name => IsCultureGate(access.Expression) ? access : null,
				QualifiedNameSyntax qualified when qualified.Right == name => RightmostName(qualified.Left) == GateType ? qualified : null,
				AliasQualifiedNameSyntax or MemberBindingExpressionSyntax or NameColonSyntax or NameEqualsSyntax => null,
				_ => InsideType(name, GateType) ? name : null,
			};

			if (reference is not null)
			{
				yield return reference;
			}
		}
	}

	private static void CheckGateReference(string path, ExpressionSyntax gate, Scan scan)
	{
		if (gate.Parent is MemberAccessExpressionSyntax access && access.IsKind(SyntaxKind.SimpleMemberAccessExpression) && access.Expression == gate)
		{
			string name = access.Name.Identifier.ValueText;
			if (name == "CurrentCount" && access.Parent is not InvocationExpressionSyntax)
			{
				return;
			}

			if (name is "Wait" or "Release" && access.Parent is InvocationExpressionSyntax call && call.Expression == access)
			{
				if (!InsideType(call, AttributeType))
				{
					scan.Add(path, call.GetLocation(), $"Gate.{name} outside {AttributeType}");
				}
				else if (name == "Wait")
				{
					scan.Waits++;
					CheckWait(path, call, scan);
				}
				else
				{
					scan.Releases++;
					CheckRelease(path, call, scan);
				}

				return;
			}
		}

		scan.Add(path, gate.GetLocation(), $"CultureGate.Gate used other than as .CurrentCount / .Wait(..) / .Release(): {Context(gate)}");
	}

	private static void CheckWait(string path, InvocationExpressionSyntax wait, Scan scan)
	{
		bool negated = wait.Parent is PrefixUnaryExpressionSyntax not && not.IsKind(SyntaxKind.LogicalNotExpression);
		ExpressionSyntax condition = negated ? (ExpressionSyntax)wait.Parent! : wait;
		if (condition.Parent is not IfStatementSyntax ifStatement || ifStatement.Condition != condition)
		{
			scan.Add(path, wait.GetLocation(), $"Gate.Wait is not the whole condition of an if (Gate.Wait(..) or !Gate.Wait(..)): {Context(wait)}");
			return;
		}

		scan.PinnedIfs.Add(ifStatement);
		if (negated)
		{
			scan.NegatedWaits++;
		}
		else
		{
			scan.PositiveWaits++;
		}

		BlockSyntax? beforeBody = EnclosingFunction(wait) is MethodDeclarationSyntax { Identifier.ValueText: "Before", Parent: TypeDeclarationSyntax { Identifier.ValueText: AttributeType } } before ? before.Body : null;
		if (beforeBody is null)
		{
			scan.Add(path, wait.GetLocation(), $"Gate.Wait is not in the body of {AttributeType}.Before (a Wait elsewhere need not run when Before does): {Context(wait)}");
		}
		else if (negated ? ifStatement.Parent != beforeBody : !IsNestedOnlyInIfBlocks(ifStatement, beforeBody))
		{
			scan.Add(path, wait.GetLocation(), negated
				? $"if (!Gate.Wait(..)) is not a direct statement of Before's body (under a condition Before could end without taking the gate): {Context(wait)}"
				: $"if (Gate.Wait(..)) is not a statement of Before's body or of the then-block of an if inside it: {Context(wait)}");
		}

		if (!negated)
		{
			StatementSyntax? first = FirstStatement(ifStatement.Statement);
			if (!IsTakeGateCall(first))
			{
				scan.Add(path, wait.GetLocation(), "the block of if (Gate.Wait(..)) does not start with TakeGate(..)");
			}
			else
			{
				scan.PinnedTakeGates.Add(((ExpressionStatementSyntax)first!).Expression);
			}

			// Exactly { TakeGate(..); return; }: anything after TakeGate, or a missing return, could
			// fall through to the second Wait and take the gate twice.
			if (ifStatement.Statement is not BlockSyntax { Statements: [_, ReturnStatementSyntax { Expression: null }] })
			{
				scan.Add(path, wait.GetLocation(), $"the block of if (Gate.Wait(..)) is not exactly {{ TakeGate(..); return; }} (it must not fall through to the second Wait): {ifStatement.Statement.ToString().ReplaceLineEndings(" ")}");
			}

			return;
		}

		if (ifStatement.Else is not null)
		{
			scan.Add(path, wait.GetLocation(), "if (!Gate.Wait(..)) has an else branch");
		}

		if (LastStatement(ifStatement.Statement) is not ThrowStatementSyntax)
		{
			scan.Add(path, wait.GetLocation(), "the block of if (!Gate.Wait(..)) does not end in throw (a return would let After release a gate this test never took)");
		}

		foreach (SyntaxNode exit in OtherExits(ifStatement.Statement))
		{
			scan.Add(path, exit.GetLocation(), $"the block of if (!Gate.Wait(..)) has an exit other than its closing throw: {Context(exit)}");
		}

		StatementSyntax? next = ifStatement.Parent is BlockSyntax block ? block.Statements.ElementAtOrDefault(block.Statements.IndexOf(ifStatement) + 1) : null;
		if (!IsTakeGateCall(next))
		{
			scan.Add(path, wait.GetLocation(), $"the statement after if (!Gate.Wait(..)) is not TakeGate(..) (it is: {next?.ToString() ?? "none"})");
		}
		else
		{
			scan.PinnedTakeGates.Add(((ExpressionStatementSyntax)next!).Expression);
		}

		// Post-acquire: xUnit v2 skips After when Before throws, so nothing may run in Before
		// after this TakeGate. It must be the last statement of Before's body.
		if (beforeBody is not null && ifStatement.Parent == beforeBody && beforeBody.Statements.LastOrDefault() != next)
		{
			scan.Add(path, wait.GetLocation(), $"the TakeGate(..) after if (!Gate.Wait(..)) is not the last statement of Before (a throw after it would leak the gate, because xUnit then skips After): last statement is {beforeBody.Statements.LastOrDefault()?.ToString().ReplaceLineEndings(" ") ?? "none"}");
		}
	}

	// The statement sits in `body` directly, or in the then-block { .. } of an if that itself
	// satisfies this, all the way up (the poison check in Before). No else, loop, try, switch,
	// using, lock or bare nested block on the way.
	private static bool IsNestedOnlyInIfBlocks(StatementSyntax statement, BlockSyntax body)
	{
		for (StatementSyntax current = statement; ;)
		{
			if (current.Parent is not BlockSyntax block)
			{
				return false;
			}

			if (block == body)
			{
				return true;
			}

			if (block.Parent is not IfStatementSyntax outer || outer.Statement != block)
			{
				return false;
			}

			current = outer;
		}
	}

	// Q1: the one exit scan for Before and After. return, goto, yield and try anywhere in `method`
	// (and, when `throws`, throw statements and throw expressions), not descending into lambdas,
	// local functions or the nodes `skip` names.
	private static IEnumerable<SyntaxNode> Exits(MethodDeclarationSyntax method, Func<SyntaxNode, bool> skip, bool throws)
	{
		return method.DescendantNodes(descendIntoChildren: node => node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) && !skip(node))
			.Where(node => node is ReturnStatementSyntax or GotoStatementSyntax or YieldStatementSyntax or TryStatementSyntax
				|| (throws && node is ThrowStatementSyntax or ThrowExpressionSyntax));
	}

	// After's body when it is exactly "try { .. } finally { CultureGate.State.Release();
	// [_ = ]Gate.Release(); }" with no catch; null otherwise. The Gate.Release statement itself is
	// checked by CheckRelease.
	private static TryStatementSyntax? PinnedAfterTry(MethodDeclarationSyntax after)
	{
		return after.Body is { Statements: [TryStatementSyntax { Catches.Count: 0, Finally.Block.Statements: [var first, ExpressionStatementSyntax] } pinned] }
			&& IsStateReleaseCall(first)
				? pinned
				: null;
	}

	// return, goto, yield and try anywhere in the block, and break / continue whose loop or switch
	// lies outside it. Lambdas and local functions are not entered: their exits leave only them.
	private static IEnumerable<SyntaxNode> OtherExits(StatementSyntax block)
	{
		foreach (SyntaxNode node in block.DescendantNodesAndSelf(descendIntoChildren: node => node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)))
		{
			bool leaves = node switch
			{
				ReturnStatementSyntax or GotoStatementSyntax or YieldStatementSyntax or TryStatementSyntax => true,
				BreakStatementSyntax or ContinueStatementSyntax => !node.Ancestors().TakeWhile(ancestor => ancestor != block)
					.Any(ancestor => ancestor is ForStatementSyntax or CommonForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax || (node is BreakStatementSyntax && ancestor is SwitchStatementSyntax)),
				_ => false,
			};

			if (leaves)
			{
				yield return node;
			}
		}
	}

	private static void CheckRelease(string path, InvocationExpressionSyntax release, Scan scan)
	{
		if (release.ArgumentList.Arguments.Count != 0)
		{
			scan.Add(path, release.GetLocation(), $"Gate.Release is called with arguments: {Context(release)}");
		}

		// The last of the two statements of the pinned finally in After (rule 4), right after
		// CultureGate.State.Release().
		if (DiscardOrSelf(release).Parent is not ExpressionStatementSyntax statement
			|| statement.Parent is not BlockSyntax { Parent: FinallyClauseSyntax { Parent: TryStatementSyntax tryStatement } } block
			|| tryStatement.Parent is not BlockSyntax body
			|| body.Parent is not MethodDeclarationSyntax { Identifier.ValueText: "After" } method
			|| PinnedAfterTry(method) != tryStatement
			|| block.Statements.IndexOf(statement) != 1)
		{
			scan.Add(path, release.GetLocation(), $"Gate.Release is not the second statement of the finally of After's pinned try {{ .. }} finally {{ CultureGate.State.Release(); Gate.Release(); }}: {Context(release)}");
		}
	}

	private static void CheckStateReference(string path, ExpressionSyntax state, Scan scan)
	{
		if (state.Parent is not MemberAccessExpressionSyntax access || !access.IsKind(SyntaxKind.SimpleMemberAccessExpression) || access.Expression != state)
		{
			scan.Add(path, state.GetLocation(), $"CultureGate.State used other than as the receiver of a member access: {Context(state)}");
			return;
		}

		string name = access.Name.Identifier.ValueText;
		if (!scan.StateMembers.Contains(name))
		{
			scan.Add(path, access.GetLocation(), $"CultureGate.State.{name} is not a member CultureGateState declares for callers ({string.Join(", ", scan.StateMembers.Order(StringComparer.Ordinal))}): {Context(access)}");
			return;
		}

		if (name is not ("Take" or "Release"))
		{
			return;
		}

		if (access.Parent is not InvocationExpressionSyntax call || call.Expression != access)
		{
			scan.Add(path, access.GetLocation(), $"CultureGate.State.{name} used without being invoked (a method group or delegate): {Context(access)}");
			return;
		}

		string expected = name == "Take" ? "TakeGate" : "After";
		if (EnclosingFunction(call) is MethodDeclarationSyntax method && method.Identifier.ValueText == expected && InsideType(method, AttributeType))
		{
			if (name == "Release")
			{
				scan.StateReleases++;
				return;
			}

			scan.Takes++;

			// F-4: the argument is evaluated after the Wait succeeded; only a bare local of TakeGate's
			// own body (holder) cannot throw there.
			if (!IsSingleBareIdentifier(call.ArgumentList, name => method.Body is not null && method.Body.Statements.OfType<LocalDeclarationStatementSyntax>().Any(local => local.Declaration.Variables.Any(variable => variable.Identifier.ValueText == name))))
			{
				scan.Add(path, call.GetLocation(), $"the argument of CultureGate.State.Take is not exactly one bare identifier naming a local declared directly in TakeGate's body (it is evaluated after the Wait succeeded, and a throw there would leak the gate): {Context(call)}");
			}

			if (!IsUnconditionalTopLevelStatement(call, method, out StatementSyntax? statement))
			{
				scan.Add(path, call.GetLocation(), $"CultureGate.State.Take is not a top-level statement of TakeGate's body reached on every path: {Context(call)}");
			}
			else if (method.Body!.Statements.LastOrDefault() != statement)
			{
				// Post-acquire: a throw after Take would leak the gate (xUnit then skips After).
				scan.Add(path, call.GetLocation(), $"CultureGate.State.Take is not the last statement of TakeGate (anything after it could throw and leak the gate, because xUnit then skips After): last statement is {method.Body.Statements.Last().ToString().ReplaceLineEndings(" ")}");
			}

			return;
		}

		scan.Add(path, call.GetLocation(), $"CultureGate.State.{name} outside {AttributeType}.{expected}: {Context(call)}");
	}

	// "x;" / "_ = x;" / "T v = x;" directly in the method body, with no return, throw or goto in any
	// statement before it. Lambdas and local functions are not entered: their exits leave only them.
	private static bool IsUnconditionalTopLevelStatement(InvocationExpressionSyntax call, MethodDeclarationSyntax method, out StatementSyntax? statement)
	{
		StatementSyntax? found = DiscardOrSelf(call).Parent switch
		{
			ExpressionStatementSyntax expression => expression,
			EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Variables.Count: 1, Parent: LocalDeclarationStatementSyntax local } } } => local,
			_ => null,
		};

		statement = found;
		return found is not null
			&& method.Body is not null
			&& found.Parent == method.Body
			&& !method.Body.Statements.TakeWhile(before => before != found)
				.SelectMany(before => before.DescendantNodesAndSelf(descendIntoChildren: node => node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)))
				.Any(node => node is ReturnStatementSyntax or ThrowStatementSyntax or ThrowExpressionSyntax or GotoStatementSyntax);
	}

	private static SyntaxNode DiscardOrSelf(InvocationExpressionSyntax call)
	{
		return call.Parent is AssignmentExpressionSyntax assignment
			&& assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
			&& assignment.Right == call
			&& assignment.Left is IdentifierNameSyntax { Identifier.ValueText: "_" }
				? assignment
				: call;
	}

	// Exactly "(name)": one positional argument with no ref / out / in, whose expression is a bare
	// identifier the predicate accepts.
	private static bool IsSingleBareIdentifier(ArgumentListSyntax arguments, Func<string, bool> accepts)
	{
		return arguments.Arguments is [{ NameColon: null, RefKindKeyword.RawKind: 0, Expression: IdentifierNameSyntax identifier }]
			&& accepts(identifier.Identifier.ValueText);
	}

	private static bool IsTakeGateCall(StatementSyntax? statement)
	{
		return statement is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "TakeGate" } } };
	}

	// Only CultureGate.State.Release() (the qualifier may be namespace-qualified). A bare State is
	// refused: inside the attribute it could be a decoy member of that name.
	private static bool IsStateReleaseCall(StatementSyntax? statement)
	{
		return statement is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0, Expression: MemberAccessExpressionSyntax access } }
			&& access.IsKind(SyntaxKind.SimpleMemberAccessExpression)
			&& access.Name.Identifier.ValueText == "Release"
			&& access.Expression is MemberAccessExpressionSyntax receiver
			&& receiver.IsKind(SyntaxKind.SimpleMemberAccessExpression)
			&& receiver.Name.Identifier.ValueText == "State"
			&& IsCultureGate(receiver.Expression);
	}

	// A simple name in expression position: not the right side of a member access or qualified
	// name, not a named argument, member binding or object-initializer target.
	private static bool IsBare(IdentifierNameSyntax name)
	{
		return name.Parent switch
		{
			MemberAccessExpressionSyntax access => access.Name != name,
			QualifiedNameSyntax qualified => qualified.Right != name,
			AliasQualifiedNameSyntax aliased => aliased.Name != name,
			MemberBindingExpressionSyntax or NameColonSyntax or NameEqualsSyntax => false,
			ExpressionColonSyntax colon => colon.Expression != name,
			AssignmentExpressionSyntax assignment when assignment.Left == name && assignment.Parent is InitializerExpressionSyntax initializer
				&& (initializer.IsKind(SyntaxKind.ObjectInitializerExpression) || initializer.IsKind(SyntaxKind.WithInitializerExpression)) => false,
			_ => true,
		};
	}

	// Syntax level: a member of an enclosing type, a primary-constructor or type parameter, or any
	// name declared inside the enclosing member (or the whole file for top-level statements).
	private static bool IsDeclaredInScope(IdentifierNameSyntax name)
	{
		string id = name.Identifier.ValueText;
		SyntaxNode scope = name.Ancestors().FirstOrDefault(ancestor => ancestor.Parent is TypeDeclarationSyntax) ?? name.SyntaxTree.GetRoot();
		return scope.DescendantNodesAndSelf().Any(node => LocalName(node) == id) || name.Ancestors().OfType<TypeDeclarationSyntax>().Any(type => type.Members.SelectMany(MemberNames).Contains(id)
			|| (type.ParameterList?.Parameters.Any(parameter => parameter.Identifier.ValueText == id) ?? false)
			|| (type.TypeParameterList?.Parameters.Any(parameter => parameter.Identifier.ValueText == id) ?? false));
	}

	private static string? LocalName(SyntaxNode node)
	{
		return node switch
		{
			VariableDeclaratorSyntax variable => variable.Identifier.ValueText,
			ParameterSyntax parameter => parameter.Identifier.ValueText,
			SingleVariableDesignationSyntax designation => designation.Identifier.ValueText,
			ForEachStatementSyntax forEach => forEach.Identifier.ValueText,
			CatchDeclarationSyntax catchDeclaration => catchDeclaration.Identifier.ValueText,
			LocalFunctionStatementSyntax localFunction => localFunction.Identifier.ValueText,
			TypeParameterSyntax typeParameter => typeParameter.Identifier.ValueText,
			FromClauseSyntax from => from.Identifier.ValueText,
			LetClauseSyntax let => let.Identifier.ValueText,
			JoinClauseSyntax join => join.Identifier.ValueText,
			JoinIntoClauseSyntax into => into.Identifier.ValueText,
			QueryContinuationSyntax continuation => continuation.Identifier.ValueText,
			_ => null,
		};
	}

	private static IEnumerable<string> MemberNames(MemberDeclarationSyntax member)
	{
		return member switch
		{
			BaseFieldDeclarationSyntax field => field.Declaration.Variables.Select(variable => variable.Identifier.ValueText),
			PropertyDeclarationSyntax property => [property.Identifier.ValueText],
			EventDeclarationSyntax eventDeclaration => [eventDeclaration.Identifier.ValueText],
			MethodDeclarationSyntax method => [method.Identifier.ValueText],
			BaseTypeDeclarationSyntax type => [type.Identifier.ValueText],
			DelegateDeclarationSyntax type => [type.Identifier.ValueText],
			_ => [],
		};
	}

	private static StatementSyntax? FirstStatement(StatementSyntax statement)
	{
		return statement is BlockSyntax block ? block.Statements.FirstOrDefault() : statement;
	}

	private static StatementSyntax? LastStatement(StatementSyntax statement)
	{
		return statement is BlockSyntax block ? block.Statements.LastOrDefault() : statement;
	}

	private static SyntaxNode? EnclosingFunction(SyntaxNode node)
	{
		return node.Ancestors().FirstOrDefault(ancestor => ancestor is BaseMethodDeclarationSyntax
			or LocalFunctionStatementSyntax
			or AnonymousFunctionExpressionSyntax
			or AccessorDeclarationSyntax
			or BasePropertyDeclarationSyntax
			or BaseFieldDeclarationSyntax
			or TypeDeclarationSyntax);
	}

	private static bool InsideType(SyntaxNode node, string typeName)
	{
		return node.Ancestors().OfType<TypeDeclarationSyntax>().Any(type => type.Identifier.ValueText == typeName);
	}

	private static bool IsCultureGate(ExpressionSyntax expression)
	{
		return RightmostName(expression) == GateType;
	}

	// CultureGateState, qualified or not, also as CultureGateState? or ref / in CultureGateState.
	private static bool IsStateType(TypeSyntax? type)
	{
		return type switch
		{
			NullableTypeSyntax nullable => IsStateType(nullable.ElementType),
			RefTypeSyntax reference => IsStateType(reference.Type),
			ScopedTypeSyntax scoped => IsStateType(scoped.Type),
			null => false,
			_ => RightmostName(type) == StateType,
		};
	}

	private static string? RightmostName(SyntaxNode node)
	{
		return node switch
		{
			IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
			QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
			AliasQualifiedNameSyntax aliased => aliased.Name.Identifier.ValueText,
			MemberAccessExpressionSyntax access when access.IsKind(SyntaxKind.SimpleMemberAccessExpression) => access.Name.Identifier.ValueText,
			_ => null,
		};
	}

	private static string Context(SyntaxNode node)
	{
		SyntaxNode shown = node.FirstAncestorOrSelf<StatementSyntax>() ?? node.FirstAncestorOrSelf<MemberDeclarationSyntax>() ?? node;
		return shown.ToString().ReplaceLineEndings(" ");
	}

	// The App.Tests sources: every .cs under the project (only the project-root bin and obj folders
	// excluded), plus every file a <Compile Include=..> item of the project's MSBuild files names.
	// MSBuild problems the scan cannot see past are added to `violations`.
	private static List<string> Sources(string root, List<string> violations)
	{
		string project = Path.Combine(root, "tests", ProjectName);

		// Case-sensitive on purpose. The SDK writes exactly "bin" and "obj" (no BaseOutputPath or
		// BaseIntermediateOutputPath override in this repository), and on Linux / macOS a "Bin" or
		// "Obj" folder is a different folder whose .cs files the default Compile glob still picks up.
		// CI (.github/workflows/ci.yml) runs only on windows-2022 today, where both spellings name one
		// folder; matching only the exact spelling costs nothing there and stays right off Windows.
		List<string> files = [.. Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
			.Where(path => Path.GetRelativePath(project, path).Split(Path.DirectorySeparatorChar) is var segments
				&& !(segments.Length > 1 && segments[0] is "bin" or "obj"))];

		// A Directory.Build.rsp can pass -p:Name=Value for any of ImportProperties (and more) on
		// every command-line build. MSBuild reads the one nearest the ENTRY project: CI builds
		// BroChoos.sln from the repository root, so one at the root applies there, and one in a
		// folder between the root and this project applies when the project is built directly.
		// Any other *.rsp is never read automatically and is not checked.
		foreach (string rsp in ResponseFiles(root, project))
		{
			violations.Add($"{Path.GetRelativePath(root, rsp)}: a Directory.Build.rsp could set MSBuild properties the scan cannot see; remove it");
		}

		// Avalonia's default globs pick up **\*.axaml with no item in the scanned files, and XamlIl
		// and Avalonia.Generators compile each one into code the scan cannot see. App.Tests has none.
		foreach (string axaml in Directory.EnumerateFiles(project, "*.axaml", SearchOption.AllDirectories)
			.Where(path => Path.GetRelativePath(project, path).Split(Path.DirectorySeparatorChar) is var segments
				&& !(segments.Length > 1 && segments[0] is "bin" or "obj")))
		{
			violations.Add($"{Path.GetRelativePath(root, axaml)}: an Avalonia .axaml file is not allowed in this project: XamlIl and Avalonia.Generators would compile it into code the scan cannot see");
		}

		foreach (string msbuild in MsBuildFiles(root, project))
		{
			string where = Path.GetRelativePath(root, msbuild);
			foreach (XElement element in XDocument.Load(msbuild).Descendants())
			{
				bool inPropertyGroup = element.Parent is { } group && MsBuildName(group.Name.LocalName, "PropertyGroup");
				bool inItemGroup = element.Parent is { } items && (MsBuildName(items.Name.LocalName, "ItemGroup") || MsBuildName(items.Name.LocalName, "ItemDefinitionGroup"));

				// A property that feeds the path of an SDK <Import> (ImportProperties) could
				// pull in an MSBuild file the scan never reads, with no <Import> in the scanned
				// files.
				if (inPropertyGroup && ImportProperties.Any(name => MsBuildName(element.Name.LocalName, name)))
				{
					violations.Add($"{where}: the property <{element.Name.LocalName}> ({element}) feeds the path of an SDK <Import>; setting it could import an MSBuild file the scan does not read");
				}

				// A property that feeds the path of an SDK <UsingTask> (TaskAssemblyProperties)
				// picks the DLL that runs a task, Csc included.
				if (inPropertyGroup && TaskAssemblyProperties.Any(name => MsBuildName(element.Name.LocalName, name)))
				{
					violations.Add($"{where}: the property <{element.Name.LocalName}> ({element}) feeds the AssemblyFile of an SDK <UsingTask>; setting it could run a task DLL, the compiler's included, that the scan does not see");
				}

				// A switch that turns on SDK targets compiling repository files into
				// obj\*.g.cs (SwitchProperties).
				if (inPropertyGroup && SwitchProperties.Any(name => MsBuildName(element.Name.LocalName, name)))
				{
					violations.Add($"{where}: the property <{element.Name.LocalName}> ({element}) switches on SDK targets that can generate code into obj\\*.g.cs, which the scan does not read");
				}

				// The target framework is pinned: another one could select other SDK targets.
				if (inPropertyGroup && (MsBuildName(element.Name.LocalName, "TargetFramework") || MsBuildName(element.Name.LocalName, "TargetFrameworks"))
					&& element.Value.Trim() != PinnedTargetFramework)
				{
					violations.Add($"{where}: <{element.Name.LocalName}> ({element}) must be exactly {PinnedTargetFramework} (the constant PinnedTargetFramework in CultureGateInvariantTests.cs); another target framework could select SDK targets the scan was not written for. A TF upgrade changes three places: BroChoos.App.Tests.csproj, BroChoos.App.csproj (the two must match) and PinnedTargetFramework");
				}

				// The SDK is pinned, by the Project's Sdk attribute and with no <Sdk> element.
				// MSBuild resolves SDK names ignoring case; a version or a list is still refused.
				if (MsBuildName(element.Name.LocalName, "Sdk"))
				{
					violations.Add($"{where}: an <Sdk> element ({element}) is not allowed in the scanned MSBuild files: it adds an SDK whose targets (Razor, Web, WindowsDesktop) can generate code the scan does not read");
				}

				if (element.Parent is null && element.Attribute("Sdk") is { } sdk && !MsBuildName(sdk.Value.Trim(), PinnedSdk))
				{
					violations.Add($"{where}: the Project Sdk attribute is \"{sdk.Value}\" but must be {PinnedSdk} (any letter case, no version, no list of SDKs): Razor, Web or WindowsDesktop targets can generate code the scan does not read");
				}

				// Csc parameters that can add code the scan does not see (CompilerProperties).
				if (inPropertyGroup && CompilerProperties.Any(name => MsBuildName(element.Name.LocalName, name)))
				{
					violations.Add($"{where}: the property <{element.Name.LocalName}> ({element}) is passed to the compiler and could add sources, analyzers or symbols the scan cannot see");
				}

				// The fail-closed preprocessor rule allows only DEBUG and TRACE, which the SDK
				// defines itself. A DefineConstants value may keep the inherited
				// $(DefineConstants) and name DEBUG or TRACE; any other part (a new symbol, or a
				// computed value) is a violation.
				if (element.Parent is { } defineGroup && MsBuildName(defineGroup.Name.LocalName, "PropertyGroup")
					&& MsBuildName(element.Name.LocalName, "DefineConstants")
					&& element.Value.Split([';', ',']).Select(part => part.Trim())
						.Any(part => part.Length > 0 && part != "$(DefineConstants)" && !KnownSymbols.Contains(part, StringComparer.Ordinal)))
				{
					violations.Add($"{where}: <DefineConstants> ({element}) adds a symbol other than {string.Join(" / ", KnownSymbols)}; this check is defence in depth: the preprocessor rule already fails any #if / #elif on another symbol, and it fails here as well so the build and the scan agree on the symbol set");
				}

				// A <ProjectReference .. OutputItemType="Analyzer"/> (or any item whose
				// OutputItemType names Analyzer, as an attribute or as child metadata, in any
				// letter case) feeds its output to the compiler as an analyzer. A computed value
				// could resolve to Analyzer, so it fails too. Only items count: an element
				// directly inside an <ItemGroup> or <ItemDefinitionGroup>.
				if (inItemGroup && UsingMetadata(element, "OutputItemType").Any(value => value.Contains("Analyzer", StringComparison.OrdinalIgnoreCase) || ComputedValue.Any(value.Contains)))
				{
					violations.Add($"{where}: an item with OutputItemType naming Analyzer ({element}) is not allowed in the scanned MSBuild files: a source generator could emit code the scan cannot see");
				}

				// Attribute names (Include, Update) are compared case-sensitively. MSBuild
				// rejects a mis-cased known item attribute (include=, INCLUDE=) as an invalid
				// attribute, a build error, rather than reading it as metadata
				// (ProjectParser.CheckMetadataAsAttributeName in ProjectParser.cs,
				// https://github.com/dotnet/msbuild/blob/main/src/Build/Evaluation/ProjectParser.cs),
				// so such an item never builds. The element name goes through MsBuildName
				// (case-insensitive) so that <using>, <COMPILE> and the like match too.
				string? include = element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Include")?.Value;
				switch (ScannedElements.FirstOrDefault(name => MsBuildName(element.Name.LocalName, name)))
				{
					case "Import":
						violations.Add($"{where}: <Import> (the scan does not follow imported MSBuild files)");
						break;
					// An analyzer or source generator runs inside the compiler and can add
					// sources the scan never sees. Analyzers that come from packages are out of
					// scope (SOURCES). A pure <Analyzer Remove=..> only drops one, so it passes;
					// Include, Update or anything else fails.
					case "Analyzer" when !IsPureRemove(element):
						violations.Add($"{where}: an MSBuild <Analyzer> item ({element}) is not allowed in the scanned MSBuild files: a source generator could emit code the scan cannot see");
						break;
					// Csc AddModules: a netmodule is merged into this assembly, code the scan
					// never reads.
					case "AddModules" when !IsPureRemove(element):
						violations.Add($"{where}: an MSBuild <AddModules> item ({element}) is not allowed in the scanned MSBuild files: the module's code would join this assembly unseen");
						break;
					// A framework's ref pack can carry analyzers and source generators
					// (Microsoft.AspNetCore.App.Ref ships Microsoft.AspNetCore.App.SourceGenerators).
					// The scanned files hold none today; the SDK's implicit Microsoft.NETCore.App
					// reference does not come from them.
					case "FrameworkReference" when !IsPureRemove(element):
						violations.Add($"{where}: an MSBuild <FrameworkReference> item ({element}) is not allowed in the scanned MSBuild files: its ref pack could add analyzers or source generators that emit code the scan cannot see");
						break;
					// The TFM targets Windows, so Microsoft.WinFX.targets is imported, and its
					// MarkupCompilePass1 runs whenever @(Page) or @(ApplicationDefinition) is not
					// empty, compiling XAML (x:Code included) into obj\*.g.cs.
					case "Page" or "ApplicationDefinition" when !IsPureRemove(element):
						violations.Add($"{where}: an MSBuild <{element.Name.LocalName}> item ({element}) is not allowed in the scanned MSBuild files: XAML markup compilation would generate code into obj\\*.g.cs, which the scan does not read");
						break;
					// Avalonia's XamlIl compiler and Avalonia.Generators compile these items into
					// code the scan cannot see (the .axaml files themselves are checked below).
					case "AvaloniaXaml" or "AvaloniaResource" when !IsPureRemove(element):
						violations.Add($"{where}: an MSBuild <{element.Name.LocalName}> item ({element}) is not allowed in the scanned MSBuild files: Avalonia XamlIl and Avalonia.Generators would compile it into code the scan cannot see");
						break;
					// A task in a target can create items at build time (<CreateItem ..><Output
					// TaskParameter="Include" ItemName="Using"/></CreateItem>, or an <ItemGroup> inside the
					// target), so Using, Compile, InternalsVisibleTo or AssemblyAttribute items could appear
					// that this static scan never sees. The scanned files hold no <Target> today, so every
					// target is banned rather than only the item-producing <Output> elements.
					case "Target":
						violations.Add($"{where}: <Target> ({element}) is not allowed in the scanned MSBuild files: its tasks could create Using, Compile, InternalsVisibleTo or AssemblyAttribute items at build time, which the scan cannot see");
						break;
					case "InternalsVisibleTo":
						violations.Add($"{where}: an MSBuild <InternalsVisibleTo> item ({element}) would let another assembly reach {GateType} outside this scan");
						break;
					// Banned outright: whatever its Include ($(..), @(..), an alias, an InternalsVisibleTo),
					// it is emitted to obj\*.AssemblyInfo.cs, which the scan does not read.
					case "AssemblyAttribute":
						violations.Add($"{where}: an MSBuild <AssemblyAttribute> item ({element}) is not allowed in this project: it would be emitted to obj\\*.AssemblyInfo.cs, which the scan does not read, and could let another assembly reach {GateType} (InternalsVisibleTo); declare assembly attributes in a scanned .cs file instead");
						break;
					// Emitted to obj\GlobalUsings.g.cs, which the scan does not read. An alias or a using
					// static there could hide [CultureIsolated] (rule 7), InternalsVisibleTo (rule 6) or
					// CultureGate members, whatever it names, so both are banned outright, as an attribute
					// or as child metadata (Static is allowed only as a literal false).
					case "Using" when UsingMetadata(element, "Alias").Any()
						|| UsingMetadata(element, "Static").Any(value => !value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)):
						violations.Add($"{where}: an MSBuild <Using> item with Alias or Static ({element}) is not allowed in this project: it would be emitted to obj\\GlobalUsings.g.cs, which the scan does not read, and could hide [{IsolatedName}], InternalsVisibleTo or {GateType} from it; write the using in a scanned .cs file instead");
						break;
					case "Using" when element.Attributes().Any(attribute => attribute.Name.LocalName is "Include" or "Update" && (UsingBannedNames.Any(name => attribute.Value.Contains(name, StringComparison.Ordinal)) || ComputedValue.Any(attribute.Value.Contains))):
						violations.Add($"{where}: an MSBuild <Using> item names {GateType}, {IsolatedName}, InternalsVisibleTo or a computed value ({element}); it would be emitted to obj\\GlobalUsings.g.cs, which the scan does not read");
						break;
					case "Compile" when include is not null:
						if (UnresolvableInclude.Any(include.Contains))
						{
							violations.Add($"{where}: <Compile Include=\"{include}\"> uses a property, item, wildcard or list the scan cannot resolve; name each file literally");
						}
						else
						{
							string path = Path.GetFullPath(Path.Combine(project, include));
							if (File.Exists(path))
							{
								files.Add(path);
							}
							else
							{
								violations.Add($"{where}: <Compile Include=\"{include}\"> names a file that does not exist ({path})");
							}
						}

						break;
					default:
						break;
				}
			}
		}

		return [.. files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)];
	}

	// The values of an item's metadata `name`, written as an attribute or as a child element
	// (matched by local name, ignoring case: see MsBuildName).
	private static IEnumerable<string> UsingMetadata(XElement element, string name)
	{
		return element.Attributes().Where(attribute => MsBuildName(attribute.Name.LocalName, name)).Select(attribute => attribute.Value)
			.Concat(element.Elements().Where(child => MsBuildName(child.Name.LocalName, name)).Select(child => child.Value));
	}

	// An item that only removes: a Remove attribute and no Include or Update (attribute names are
	// case-sensitive, see Sources).
	private static bool IsPureRemove(XElement element)
	{
		return element.Attribute("Remove") is not null && element.Attribute("Include") is null && element.Attribute("Update") is null;
	}

	// MSBuild item type names and item metadata names are case-insensitive ("Item metadata and
	// Item/Property names are not case-sensitive", https://learn.microsoft.com/visualstudio/msbuild/item-definitions;
	// "Metadata name matching is case-insensitive", https://learn.microsoft.com/visualstudio/msbuild/msbuild-items),
	// so <using .. STATIC="true"/> is the same item as <Using .. Static="true"/>. Attribute names of
	// the item element itself (Include, Update, Remove) are case-sensitive and are not compared here.
	// Import and Target are project elements, not items; they are matched ignoring case as well,
	// which fails closed.
	private static bool MsBuildName(string actual, string expected)
	{
		return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
	}

	// The csproj, and each Directory.Build.props / .targets / Directory.Packages.props from the
	// project folder up to the repository root (a superset of what MSBuild imports).
	private static IEnumerable<string> MsBuildFiles(string root, string project)
	{
		yield return Path.Combine(project, ProjectName + ".csproj");
		for (DirectoryInfo? directory = new(project); directory is not null; directory = directory.Parent)
		{
			foreach (string candidate in MsBuildFileNames.Select(file => Path.Combine(directory.FullName, file)).Where(File.Exists))
			{
				yield return candidate;
			}

			if (string.Equals(directory.FullName.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
			{
				yield break;
			}
		}
	}

	// Each Directory.Build.rsp from the project folder up to the repository root.
	private static IEnumerable<string> ResponseFiles(string root, string project)
	{
		IEnumerable<string> found = [];
		for (DirectoryInfo? directory = new(project); directory is not null; directory = directory.Parent)
		{
			found = found.Concat(directory.EnumerateFiles("Directory.Build.rsp").Select(file => file.FullName));
			if (string.Equals(directory.FullName.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
			{
				break;
			}
		}

		return found.Distinct(StringComparer.OrdinalIgnoreCase);
	}

	private static string RepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BroChoos.sln")))
		{
			directory = directory.Parent;
		}

		return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate BroChoos.sln from the test execution directory.");
	}

	private sealed class Scan(string configuration)
	{
		public string Configuration => configuration;

		public List<string> Violations { get; } = [];

		public HashSet<string> StateMembers { get; } = new(StringComparer.Ordinal);

		public int Waits { get; set; }

		public int PositiveWaits { get; set; }

		public int NegatedWaits { get; set; }

		public int Releases { get; set; }

		public int Takes { get; set; }

		public int StateReleases { get; set; }

		public int TakeGateUses { get; set; }

		public int IsolatedUses { get; set; }

		public List<ExpressionSyntax> PinnedTakeGates { get; } = [];

		public List<IfStatementSyntax> PinnedIfs { get; } = [];

		public void Add(string path, Location location, string message)
		{
			Violations.Add($"[{configuration}] {path}:{location.GetLineSpan().StartLinePosition.Line + 1}: {message}");
		}

		public void ExpectCount(string what, int actual, int expected)
		{
			if (actual != expected)
			{
				Violations.Add($"[{configuration}] expected exactly {expected} {what}, found {actual}");
			}
		}
	}
}
