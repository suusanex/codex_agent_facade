---
name: dotnet-file-based-apps
description: >
  File-based apps（.cs ファイルを dotnet で直接ビルド/実行する形式）を作成・修正するときに使う。
  File-based apps / File-based App / run C# file directly / #:-directives / #:package / #:include / dotnet run file.cs / dotnet publish file.cs の話題に反応してロードする。
  ルール: 1) NuGet 参照は必ず「#:package」で書く 2) .csproj は作らない（作成提案もしない） 3) 複数ファイル化は .NET 11 SDK 以降でのみ使う。
argument-hint: "[target.cs] [やりたいこと] [必要なNuGetパッケージ]"
user-invokable: true
disable-model-invocation: false
# Copyright (c) 2026 suusanex (GitHub UserName)
# SPDX-License-Identifier: CC-BY-4.0
# License: https://creativecommons.org/licenses/by/4.0/
# Source: https://github.com/suusanex/agent_skills_dotnet_utility
---

# File-based apps Skill

## 0) 参照元（最重要）
- 主要な文法・CLI 操作・適用 SDK は、公式ドキュメントに集約されている  
  https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps
- 複数ファイル対応（`#:include`）は、.NET 11 Preview 3 の公式リリースノートに記載されている  
  https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/preview3/sdk.md#file-based-apps-can-be-split-across-files

このスキルは「上記ドキュメントの内容を逸脱しない」ことを第一優先にする。

補足:
- 正式名称は File-based apps とし、バージョン番号を名称に含めない
- 機能の利用前提は .NET 10 SDK 以降とする

## 1) 絶対に守るルール（よくミスる所）
### 1-1) NuGet パッケージ参照は `#:package` だけ
- 例（OK）:
  - `#:package Newtonsoft.Json`
  - `#:package Serilog@3.1.1`
  - `#:package Spectre.Console@*`
- 禁止（混同しやすいので絶対に出さない）:
  - csproj の `<PackageReference .../>`
  - `#r "nuget: ..."`（C# scripting系の書き方）
  - `dotnet add package ...` を前提にした説明

### 1-2) `.csproj` は作らない
- File-based apps は C# ファイル先頭の `#:` ディレクティブで構成を記述する方式
- 出力として `.csproj` の生成・追加を提案しない
- ただし、ユーザーが「プロジェクト形式へ変換したい」「.csproj にしたい」のように変換意思を明確に表現した場合のみ、公式の変換コマンドの存在を示す（勝手に変換はしない）

### 1-3) ファイル構成は SDK バージョンで分岐する
- 基本方針は 1 ファイル完結とする
- ただし、共通ロジックを複数のスクリプトで使い回すべき場合は、.NET 11 SDK 以降（Preview 含む）が利用可能な環境に限って `#:include` を使った複数ファイル構成を許可する
- 実装環境で .NET 11 SDK 以降が使えない、または使用可否を確認できない場合は、1 ファイル原則のまま実装する
- 複数ファイル化は、重複する共通ロジックの切り出しが主目的である場合に限る。不要な分割は避ける

## 2) 正しい実行・ビルド確認のやり方
### 2-1) 実行（正）
- 基本:
  - `dotnet run file.cs`
- カレントに既存の `.csproj` がある場合の誤動作回避（優先推奨）:
  - `dotnet run --file file.cs`

### 2-2) 動かさずに「ビルド確認だけ」したい（正）
- `dotnet publish file.cs`
  - 実行はしない。ビルド/発行が通ることを確認する用途に使う

## 3) 生成するときの作法（Copilot の手順）
1. まず「対象は File-based app で、csproj は作らない」ことを宣言してから作業する
2. 正式名称は File-based apps とし、バージョン番号を名称に含めない
3. 実装前に、複数ファイルを使ってよいかを SDK 条件で判定する
4. .NET 11 SDK 以降（Preview 含む）が使えると明確な場合だけ、必要に応じて `#:include` による複数ファイル構成を使う
5. 上記条件を満たさない場合、生成物は原則 **1ファイル（target.cs）** にまとめる
6. `#:` ディレクティブは **C# ファイルの先頭**にまとめて配置する
7. NuGet が必要なら必ず `#:package` を使い、可能ならバージョンも明示して再現性を上げる
8. ユーザーの希望がなければ、実行コマンドは `dotnet run --file target.cs` を提示する
9. 「ビルド確認だけ」と言われたら `dotnet publish target.cs` を提示する

## 4) ミニ雛形（例）
> 必要に応じてディレクティブ行を増減してよい（ただし NuGet 参照は `#:package` 固定）

### 4-1) 標準形（.NET 10 SDK 以降 / 1 ファイル）

#:property TargetFramework=net10.0
// #:package Spectre.Console@*

using System;

Console.WriteLine("Hello, file-based app!");

### 4-2) 複数ファイル形（.NET 11 SDK 以降のみ）

target.cs:

#:property TargetFramework=net10.0
#:include helpers.cs

Console.WriteLine(Helpers.GetMessage());

helpers.cs:

internal static class Helpers
{
  public static string GetMessage() => "Hello from shared helper!";
}

この形式は、実装環境で .NET 11 SDK 以降（Preview 含む）が利用可能だと確認できた場合だけ使う。
確認できない場合は、`helpers.cs` の内容も `target.cs` に戻して 1 ファイルで実装する。