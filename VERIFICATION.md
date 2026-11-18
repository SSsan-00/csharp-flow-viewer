# 検証結果

2026-11-18、Debian 13 x64、.NET SDK 9.0.321 / Runtime 9.0.23、Node.js 24.22.1、Chromium 156.0.8078.79 で実施しました。

| 検証 | 結果 |
| --- | --- |
| Release ビルド | ソリューションと架空サンプルを個別にビルド。どちらもエラー 0、警告 0 |
| MSTest | 14 件成功。条件の真偽、短絡評価、三項演算子、switch、4 種のループ、制御移動、代入内の呼び出し、getter／setter の評価順、finally の継続先、再スローとフィルター、到達不能、同名オーバーロードと動的呼び出しを検証 |
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

巨大メソッドは 8,002 ノード、12,001 遷移、690,000 文字の Mermaid を生成しました。この規模ではプレビューがノード一覧に切り替わり、全体を `.mmd` に書き出せます。計測には MSBuild のプロジェクト読み込み、WPF の表示、WebView2 の描画時間を含みません。数値は性能保証ではありません。

## Windows で未確認の項目

Windows の実機での外観、画面サイズ変更やペイン操作、WPF のソース表示との連動、クリップボードと保存ダイアログ、テーマの再起動時復元、エラー表示は未確認です。Windows クリーン環境での .NET Desktop Runtime／対象 SDK／WebView2 の組み合わせ、WebView2 での図の描画と欠落時のノード一覧切り替えも未確認です。
