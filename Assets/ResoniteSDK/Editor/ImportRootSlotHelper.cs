using ResoniteLink;
using System.Threading.Tasks;

// 2026-08-08 (Tanossy指摘「被せるはずだったよね？」/「外だしして」): SceneConverterの
// EnsureImportRootSlot()が呼ぶ、既存の「Unity Import」ルートスロット探索ロジック。以前は
// SceneConverter.cs内の非static privateメソッドだったが、Link(接続)とスロット名以外の
// インスタンス状態には依存していなかったため、パラメータ渡しのstaticメソッドとしてここへ
// 外だしした。公式SDK側の変更はEnsureImportRootSlot()内の1呼び出し行だけで済む。
//
// Best-effort lookup for a pre-existing "Unity Import" slot directly under World Root, from a
// *previous* session (this or an earlier Editor session/connection). Returns its ID if found,
// or null if not found / not connected / the query itself fails for any reason - the caller
// always has a safe fallback (just allocate a fresh ID), so a failure here should never block
// conversion.
public static class ImportRootSlotHelper
{
    public static string TryFindExistingId(LinkInterface link, string slotName)
    {
        if (link == null || !link.IsConnected)
            return null;

        try
        {
            var response = Task.Run(async () => await link.GetSlotData(new GetSlot()
            {
                SlotID = "Root",
                Depth = 1,
                IncludeComponentData = false,
            })).GetAwaiter().GetResult();

            if (response == null || !response.Success || response.Data?.Children == null)
                return null;

            foreach (var child in response.Data.Children)
                if (child.Name?.Value == slotName)
                    return child.ID;
        }
        catch
        {
            // Swallow: worst case we fall back to the pre-existing behavior (allocate fresh,
            // leave the stale tree orphaned) rather than fail the whole conversion over a
            // best-effort cleanup lookup.
        }

        return null;
    }
}
