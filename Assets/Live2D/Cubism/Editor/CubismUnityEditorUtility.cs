using System.IO;
using System.Text;
using UnityEditor;

namespace Live2D.Cubism.Editor
{
    public class CubismUnityEditorUtility
    {
        /// <summary>
        /// Projectウィンドウで現在選択しているディレクトリのパスを取得。
        /// Projectウィンドウ以外が選択されていたり、何も選択されていない場合、返す値はAssets直下。
        /// </summary>
        /// <returns>Projectウィンドウで現在のディレクトリのパス</returns>
        public static string GetCurrentDirectoryPath()
        {
            var activeObject = Selection.activeObject;
            var currentDirectoryPath = ((activeObject == null)
                ? "Assets"
                : AssetDatabase.GetAssetPath(activeObject));

            if (string.IsNullOrEmpty(currentDirectoryPath))
            {
                currentDirectoryPath = "Assets";
            }
            else if (!Directory.Exists(currentDirectoryPath))
            {
                currentDirectoryPath = currentDirectoryPath.Replace("/" + Path.GetFileName(currentDirectoryPath), "");
            }

            return currentDirectoryPath;
        }

        /// <summary>
        /// Deterministically derives a 32-bit id from <paramref name="key"/>.
        /// Used where a stable, session-independent stand-in for Object.GetInstanceID() is needed:
        /// CubismFadeMotionList.MotionInstanceIds is a serialized int[] and is matched against
        /// AnimationEvent.intParameter (a fixed int field), neither of which can hold the 64-bit
        /// EntityId that replaced GetInstanceID().
        /// </summary>
        /// <param name="key">A value that uniquely identifies the motion, e.g. its asset path.</param>
        /// <returns>A deterministic 32-bit id.</returns>
        public static int GetStableId(string key)
        {
            unchecked
            {
                const uint offsetBasis = 2166136261;
                const uint prime = 16777619;

                var hash = offsetBasis;
                foreach (var b in Encoding.UTF8.GetBytes(key))
                {
                    hash = (hash ^ b) * prime;
                }

                return (int)hash;
            }
        }
    }
}
