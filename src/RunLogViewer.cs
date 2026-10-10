#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property OutputType=Exe
// File-based app の既定は PublishAot で、native linker と self-contained 配置を要求する。
// publish-facade は framework-dependent で配置するため、Facade と同じく無効にする。
#:property PublishAot=false
#:property NoWarn=CA2266
#:include RunLogViewerSession.cs

return await RunLogViewerProgram.RunAsync(args, Console.Out, Console.Error, CancellationToken.None);
