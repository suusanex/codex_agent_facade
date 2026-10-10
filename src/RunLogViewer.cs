#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property OutputType=Exe
#:property NoWarn=CA2266
#:include RunLogViewerSession.cs

return await RunLogViewerProgram.RunAsync(args, Console.Out, Console.Error, CancellationToken.None);
