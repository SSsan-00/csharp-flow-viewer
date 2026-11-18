# csharp-flow-viewer

Windows で、C# メソッドの制御フローを Roslyn で解析して Mermaid で表示する開発ツールです。左でプロジェクトのファイルとメソッドを選び、中央でフローをたどり、右で対応するソースを確認できます。Razor Pages では `.cshtml.cs` の PageModel が対象です。

**このアプリは指定どおり .NET 9.0 を使用します。.NET 9 の Microsoft サポートは 2026 年 11 月 10 日に終了しました。** 同梱ランタイムも同じライフサイクルです。 [Microsoft .NET サポートポリシー](https://dotnet.microsoft.com/platform/support/policy/dotnet-core)

## 利用環境

| 用途 | 必要なもの |
| --- | --- |
| 発行済み EXE の実行 | Windows 10 バージョン 1809 以降の x64。アプリ用の .NET 9、Windows Desktop Runtime、WebView2 が読み込む Windows SDK の .NET ライブラリは EXE に同梱 |
| プロジェクトの解析 | `dotnet` が PATH にあり、対象プロジェクトを理解できる .NET SDK 8 以降、必要なターゲティングパックおよび復元済みの参照が利用できること。検証に使用した SDK は 9.0.321 |
| フロー図のプレビュー | Microsoft Edge WebView2 Evergreen Runtime x64。利用できない場合はノード一覧、ソース連動、Mermaid 出力を利用可能 |
| このリポジトリをビルド | .NET 9 SDK 9.0.300 以降の 9.0 系。Node.js 22.12 以降は Mermaid バンドルを再生成する場合とブラウザテストで使用 |

SDK の選択はプロジェクトのディレクトリを起点とする `global.json` とインストール済み SDK に依存します。異なる環境では MSBuild の評価や参照の解決結果も変わります。SDK スタイルのプロジェクトで検証しています。解析には Roslyn 4.14（C# 13）を使用します。Visual Studio 固有の設計時ターゲット、ワークロードや独自 NuGet SDK を使うプロジェクトは読み込みに失敗する場合があります。 [WebView2 Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/#download-section)

## 使い方

1. **プロジェクトを開く…**（Ctrl+O）で信頼できる `.csproj` を選びます。上部に評価に使った SDK と、構成・対象フレームワークを表示します。候補が複数ある場合は切り替えられます。
2. 左の一覧で `.cs` または `.cshtml.cs` を選択します。ファイル検索欄ではファイル名やプロジェクトからの相対パスで絞り込めます。メソッド検索欄では名前、シグネチャ、行番号で絞り込めます。オーバーロードは型・引数・行番号で区別できます。
3. 中央の図はホイール、拡大・縮小ボタン、ドラッグで操作します。**全体** でフィットします。ペイン間の仕切りはドラッグで調節できます。図のノードを選ぶと右のソースで範囲を表示します。
4. 同じファイル内の実装を一意に特定できた呼び出しでは **呼び出し先へ** から別図に移動できます。**‹**（Alt+←）で戻れます。別ファイルのメソッドを確認するには対象ファイルを左で選択します。
5. **コピー** と **.mmd 保存** で Mermaid テキストを取得できます。下部の **診断** で解析や描画の詳細を確認できます。右上のテーマボタンでライト／ダークを切り替えます。

架空のサンプルは `samples/OrderProcessing/OrderProcessing.csproj` です。必要に応じて `dotnet restore samples/OrderProcessing/OrderProcessing.csproj` を実行し、`Pages/Order.cshtml.cs` の `OnPost` と `Submit` を開いてください。サンプルを起動する必要はありません。

## 図の読み方と解析範囲

- `SemanticModel` と `ControlFlowGraph` を用います。条件辺の True／False は CFG の `ConditionKind` と遷移種別から求めます。短絡評価、三項演算子、switch、ループの戻り先を保持します。
- 単純な代入と代入値は図に表示されません。代入内の呼び出しやプロパティの getter／setter は実行順で表示します。正確な引数や値は右のソースで確認してください。`break`、`continue`、値のない `return`、再スローは Roslyn CFG が通常辺にまとめることがあるため、一時的な解析専用コンパイルで位置を追跡します。
- 呼び出しノードに別メソッドの本体は展開されません。外部ファイル、デリゲート、動的呼び出し、実装を特定できない仮想呼び出しには同一ファイル内の移動リンクが付きません。再帰と循環は明示的な移動でたどれます。
- 通常遷移と return では CFG が示す順に finally を経由します。同じ finally が別の継続先を持つ場合は図で複数回表示されます。明示した例外型とフィルターなしのハンドラーを特定できる場合は、必要な finally から catch に接続します。実行時型・例外フィルター・暗黙の例外が関係する場合は、例外探索や巻き戻しを境界として表示します。独立した catch、filter、finally の入口は実行時に到達し得る領域です。全ての例外の発生位置やハンドラー選択、その後の実行を予測する機能はありません。
- 破線は Roslyn または定数条件で判断した到達不能な経路です。コンパイル条件で除外されたコードはソースに残り、図の対象外です。抽象メソッドなど実装がない選択肢、未解決の式、参照不足、複合パターン内部など個別表示ができない場合は、診断で影響を表示します。
- メソッド、コンストラクター、演算子、明示的アクセサーと、対応するメソッド内のローカル関数を対象にします。トップレベルの本体、ラムダや匿名関数の別図、自動実装アクセサーの本体、Razor／HTML／JavaScript、コンパイラーが生成する async・iterator の状態機械は対象外です。分解代入や暗黙のインデクサーは一部表示されます。
- プレビュー上限は 650 ノード、1,000 遷移または Mermaid 380,000 文字です。上限を超えるとノード一覧を表示します。全体の `.mmd` は出力でき、外部レンダラーではそちらの上限設定も必要です。解析中に finally の継続先が 4,096 に達した場合は部分解析になります。

## ソースと通信

**信頼できるプロジェクトだけを開いてください。** 読み込みでは MSBuild によるプロパティ評価と設計時ビルドが行われます。プロジェクト、インポート先や SDK のカスタムターゲット・タスクは、そのプロセスの権限で動く可能性があります。アプリはソースのビルド成果物を起動せず、Roslyn に渡すプロジェクトからアナライザー／ソースジェネレーターを外します。その生成物が参照解決に必要な場合は部分解析になります。

アプリは対象の NuGet restore を開始せず、NuGet SDK リゾルバー、Workload リゾルバーと .NET CLI テレメトリを無効にします。カスタム MSBuild タスクや OS をネットワークから隔離する機能はありません。接続を禁止する業務環境では OS の通信制限下で使用してください。

Mermaid のライブラリとページを EXE に同梱します。WebView2 のアプリページはメモリから配信し、そのページの外部リクエスト・ナビゲーションをブロックします。ソースと Mermaid の内容を外部サービスに送信しません。アプリは解析ログやソースを保存しません。テーマだけを `%LOCALAPPDATA%\csharp-flow-viewer\theme` に保存します。コピーと保存は利用者の操作でソース由来のラベルをクリップボードや指定ファイルに書き出すため、共有前に内容を確認してください。WebView2 は一時プロファイルを使い、終了時に削除を試みます。

## ビルド・発行

```powershell
dotnet build csharp-flow-viewer.sln -c Release
dotnet test tests/Flow.Tests -c Release
dotnet publish src/Flow.Desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o artifacts/win-x64
```

`artifacts/win-x64/csharp-flow-viewer.exe` が配布対象です。WPF を選んだ理由はネイティブの Windows ウィンドウ、サイズ調整可能なペイン、ソースエディターを利用でき、Windows Desktop Runtime を単一ファイルに同梱できるためです。WPF と Roslyn の別プロセス用資産を展開するため `IncludeAllContentForSelfExtract` を有効にしています。実行先のユーザーにはバンドル展開用の一時ディレクトリへの書き込み権限が必要です。Windows 上では署名されていない実行ファイルに対する OS の警告が出る場合があります。

責務は `Flow.Analysis`（MSBuild／Roslyn）、`Flow.Model`（図と対応ソース範囲）、`Flow.Mermaid`（安定した ID とエスケープを持つ出力）、`Flow.Desktop`（WPF、AvalonEdit、ローカルプレビュー）に分かれています。

Mermaid を更新する場合は、`npm --prefix tools/preview ci` と `npm --prefix tools/preview run build` で同梱 JS とライセンス一覧を再生成します。ブラウザテストは `CFV_MERMAID_FIXTURE` に生成先の絶対パスを指定して `dotnet test tests/Flow.Tests` を実行し、`CFV_CHROMIUM` に Chromium の実行ファイルを指定して `npm --prefix tools/preview test` を実行します。

[MIT ライセンス](LICENSE) · [サードパーティー通知と配布ライセンス](THIRD-PARTY-NOTICES.md) · [検証結果と未確認事項](VERIFICATION.md)
