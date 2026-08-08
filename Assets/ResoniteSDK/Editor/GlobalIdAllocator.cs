// 2026-08-08 (実機で発覚 / Tanossy指摘「外だしして」): UniqueSessionIdはResoniteLinkの
// ハンドシェイクがサーバー側から割り当てる値で、こちら側で完全制御できない(再接続の仕方によっては
// 同じ値が返ってくることが実機で確認された)。公式SDK(SceneConverter.cs)の従来のID採番カウンタは
// SceneConverterインスタンス単位でリセットされていたため、UniqueSessionIdが変わらないまま
// 新しいSceneConverterが作られると、ID生成が完全に前回と同じ文字列列("Unity_0__0"等)を再現し、
// まだサーバー側に残っている前回の同名オブジェクトと衝突して"ID '...' is already in use"の
// FATAL ERRORでコンバータがIsCorrupted状態に陥る実害があった。
//
// プロセス全体で単調増加するこのstaticカウンタに切り替えることで、SceneConverterが何度作り直
// されようと、このUnity Editorプロセスの生存期間中は同じ番号が二度と生成されないことを保証する。
// 公式SDK側の変更はSceneConverter.AllocateId()内の採番元をこのクラスへ差し替える1行だけ。
public static class GlobalIdAllocator
{
    static long _pool;

    public static long Next() => System.Threading.Interlocked.Increment(ref _pool) - 1;
}
