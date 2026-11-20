# 検証結果

## Razor / JavaScript 解析の追加（2026-11-19）

Linux x64、.NET SDK 9.0.321 / Runtime 9.0.23 で実施しました。以下は今回の変更に対する検証結果です。

| 検証 | 結果 |
| --- | --- |
| Release ビルド | WPF プロジェクトを含むソリューションと架空サンプルを個別にビルド。どちらもエラー 0、警告 0 |
| MSTest | MSBuild の別プロセスを必要としない 56 件が成功。既存の C# 解析に加え、Razor の `@:` / `<text>`、元ソースの位置、JavaScript のイベント・分岐・XHR・スコープ、動的値や不正な入力の扱いを検証 |
| ページとハンドラーの対応 | 実際の ASP.NET Core のアセンブリを参照するプロセス内の Roslyn ワークスペースを使い、名前空間の優先順位、継承したハンドラー、`NonHandler`、外部リンクファイルの制約を検証 |
| 送信先の判定 | Form / Anchor Tag Helper の有効・無効、フォームの既定 HTTP メソッド、同一ページへの XHR、フラグメント、`base`、動的 URL、MVC / 名前付きルートを現在の PageModel へ誤接続しないことを検証 |

再現コマンドは次のとおりです。先に依存パッケージを復元してください。

```sh
dotnet build csharp-flow-viewer.sln -c Release --no-restore -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false
dotnet build samples/OrderProcessing/OrderProcessing.csproj -c Release -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false
dotnet test tests/Flow.Tests/Flow.Tests.csproj -c Release --no-build --no-restore -m:1 --filter 'FullyQualifiedName!~Flow.Tests.ProjectTests'
```

`ProjectTests` の 2 件は、この実行環境が Roslyn の MSBuild BuildHost に必要な名前付きパイプを許可しないため、`SocketException: Permission denied` で実行できませんでした。プロジェクト項目の評価を確認する既存テストと、実際のプロジェクトから Razor のモデル・継承ハンドラーを解決する追加テストを残しています。通常の開発環境では上記の `--filter` を外して確認してください。プロセス内の検証は、この 2 件の統合テストを代替するものではありません。

今回の `.cshtml` 選択、イベント・関数選択、PageModel への移動と復帰について、Windows 上での画面操作は未実施です。今回の変更を含む Windows 単一 EXE の発行・起動も未実施です。下記の過去の Windows 検証結果は、今回追加した機能の動作確認には含めていません。

## これまでの検証記録

2026-11-18、Debian 13 x64、.NET SDK 9.0.321 / Runtime 9.0.23、Node.js 24.22.1、Chromium 156.0.8078.79 で実施しました。

| 検証 | 結果 |
| --- | --- |
| Release ビルド | ソリューションと架空サンプルを個別にビルド。どちらもエラー 0、警告 0 |
| MSTest | 19 件成功。条件の真偽、短絡評価、三項演算子、switch、4 種のループ、制御移動、代入内の呼び出し、getter／setter の非表示、正規表現で呼び出しを除外した場合の引数評価と分岐、finally の継続先、再スローとフィルター、到達不能、同名オーバーロードと動的呼び出しを検証 |
| MSBuild 統合テスト | 1 つのテスト内で net9.0 / netstandard2.1、Debug / Review、`#if`、別 TFM のプロジェクト参照、Compile 除外項目を検証 |
| Chromium | 同梱する Mermaid 12.3.0 で実際にパース・SVG 描画。日本語、引用符、記号、HTML に見えるリテラル、辺ラベル、ノード選択イベント、ドラッグ、ズーム、リサイズとライト／ダークを検証 |
| 単一ファイルの解析ホスト | 同じ解析ライブラリを組み込んだ Linux x64 の self-contained 単一ファイルの試験プログラムで、展開された Roslyn ビルドホストからサンプルの `OnPost` を解析。13 ノードと 15 遷移、同一ファイルの `Submit` へのリンクを確認 |
| Windows x64 発行物 | 圧縮した単一 EXE をクロス発行。PE の x64 ヘッダーとバンドルのマニフェストを検査し、.NET／WPF、WebView2 ローダー、Roslyn の別プロセス用資産が含まれることを確認 |

## Windows x64 単一 EXE の起動確認

修正前の EXE は、GitHub Actions の Windows Server 2025（OS ビルド 26100）で起動直後に終了しました。.NET Runtime のイベントには、WebView2 の画面部品が読み込む `Microsoft.Windows.SDK.NET.dll` が見つからないことが記録されていました。 [修正前の実行ログ](https://github.com/SSsan-00/csharp-flow-viewer/actions/runs/44236177021)

Windows SDK のバージョンを対象フレームワークに指定し、必要な DLL を EXE に同梱しました。修正後は Linux でクロス発行した単一 EXE を同じ Windows ランナーで検証しました。通常設定と WebView2 の実行フォルダーを存在しない場所に指定した設定の両方で、空の作業フォルダーからメインウィンドウを開き、正常に終了できました。展開先の `Microsoft.Windows.SDK.NET.dll` と `WinRT.Runtime.dll` も確認しました。 [修正後の実行ログ](https://github.com/SSsan-00/csharp-flow-viewer/actions/runs/44236671989)

検証した EXE は 93,165,251 バイト（約 89 MiB）です。SHA-256 は `28b6e9650dfa4b7ae77585dfadbd6f30d7f97a4c135f083e77f9c7f7c914fe2a` です。

## 約 2 万行の計測

架空コードをメモリでコンパイルした Release テストでの一例です。表の一覧作成には、ソースの生成、Roslyn コンパイルとメソッド一覧の作成を含みます。

| 入力 | 一覧作成まで | 選択した 1 メソッドの CFG と表示モデル |
| --- | ---: | ---: |
| 小さなメソッド 4,000 件 | 311 ms | 1 ms 未満 |
| 4,000 個の条件を含む巨大メソッド 1 件 | 196 ms | 573 ms |

巨大メソッドは 8,002 ノード、12,001 遷移、690,000 文字の Mermaid を生成しました。この規模では軽量な SVG レイアウトを使用し、全体を `.mmd` に書き出せます。計測には MSBuild のプロジェクト読み込み、WPF の表示、WebView2 の描画時間を含みません。数値は性能保証ではありません。

## Windows のプレビューと追跡設定

Windows Server 2025 のランナーで架空の注文プロジェクトを開き、メソッド選択後に WebView2 内で 13 個の SVG ノードを確認しました。正規表現の設定を JSON に書き出して読み戻し、編集画面に表示されることも確認しました。 [Windows の検証ログ](https://github.com/SSsan-00/csharp-flow-viewer/actions/runs/44239155439)

## エディタ操作とフロー図の注釈

Windows Server 2025 のランナーで、サンプルのプレビューに 13 ノードが描画されました。折り返し、行移動、エディタの検索コマンド、WebView2 の右クリックメニューからのマーク、テーマ切り替え時のズームと位置の維持を確認しました。検索と行移動の検証ではコマンドと処理を直接呼び出しました。 [Windows の検証ログ](https://github.com/SSsan-00/csharp-flow-viewer/actions/runs/44248604874)

Chromium では括弧を含むラベル、注釈のスタイル、8,002 ノードとループを含む SVG の描画、末尾ノードへの移動を確認しました。MSTest では、グレーアウトが別経路と共有する合流点やループ条件に波及しないことを確認しました。

## ファイル状態の復元とナビゲーション

.NET SDK 9.0.321 で Release ビルドが成功し、MSTest 22 件が通過しました。追加分では、`switch` の判定式・ガード・共有経路のグレーアウト、演算子を省いたときの呼び出し順、ファイル状態の JSON 保存と構成別の分離を確認しました。Chromium では 8,002 ノードの図で検索と縦スクロールを操作し、別の図へ切り替えた直後のマーク、グレーアウト領域内のマーク、保存した倍率と位置の復元を確認しました。

Windows Server 2025 の GitHub Actions ランナーで WPF の Release ビルドを起動しました。マウス入力でソースをクリックして対応するノードが選択されること、呼び出し元ノードへの復帰、メソッドとファイルを切り替えた後のマーク、ファイルを選び直した後のメソッド・選択ノード・ソース位置・図の位置と倍率・マークの復元を確認しました。今回の Windows x64 単一 EXE はクロス発行し、x64 PE ヘッダーを確認しました。今回の単一 EXE 自体の起動と、アプリの再起動をまたぐ復元操作は Windows で未実施です。 [Windows の検証ログ](https://github.com/SSsan-00/csharp-flow-viewer/actions/runs/44256758668)

## Windows で未確認の項目

Windows の実機での外観、画面サイズ変更やペイン操作、WPF のソース表示との連動、クリップボードと保存ダイアログ、テーマの再起動時復元、エラー表示は未確認です。Windows クリーン環境での .NET Desktop Runtime／対象 SDK／WebView2 の組み合わせと、WebView2 欠落時のノード一覧切り替えは未確認です。

## グレーアウト注釈と再訪問の改善

グレーアウトの起点ごとに探索する旧実装と、到達可能な経路の支配関係をまとめて求める新実装を、同じ .NET SDK 9.0.321 / Linux x64 / Release プロセスで比較しました。全ノードをつないだ単一路に等間隔の起点を設定し、ウォームアップ後3回の中央値を取りました。割り当て量は計算スレッドの累積値であり、ピークメモリや画面全体の応答時間ではありません。

| ノード / 起点 | 旧実装 | 新実装 |
| --- | ---: | ---: |
| 8,002 / 100 | 106.49 ms / 66.35 MiB | 6.71 ms / 2.63 MiB |
| 8,002 / 1,000 | 813.47 ms / 650.57 MiB | 3.28 ms / 2.60 MiB |
| 32,002 / 100 | 416.54 ms / 272.32 MiB | 14.93 ms / 10.86 MiB |

各比較で同じノード集合になること、合流・ループ・複数入口・未接続領域・switch 一致辺を含む決定的な乱数ケース700件で旧ルールと等価であることを確認しました。注釈はバックグラウンドで計算し、同じ図・起点の計算は再描画時に再利用します。

C# のファイルと図を同じプロジェクトセッション内で再利用し、キャッシュ件数と図のサイズに上限を設けています。再読み込みと構成・対象フレームワーク切り替えでファイルと図を破棄し、追跡設定変更で図を破棄します。Mermaid出力の初回生成もバックグラウンド処理に移しました。これは #3 の再訪問改善です。実プロジェクトの初回ロード、Windows描画全体の比較、操作単位のキャンセルは未検証・未対応です。
