using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using TMPro;

[Serializable]
public class Atom
{
    public int atomicNum;
    public Vector3 position;
    public GameObject gameObject;
}

[Serializable]
public class Bond
{
    public int from, to;
    public int bondType; // 1=single, 2=double, 3=triple
}

public class SdfLoader : MonoBehaviour
{
    public GameObject atomPrefab;
    public GameObject bondSinglePrefab;
    public GameObject bondDoublePrefab;
    public GameObject bondTriplePrefab;
    public Transform container;
    public TMP_InputField inputField;

    private List<Atom> atoms = new List<Atom>();
    private List<Bond> bonds = new List<Bond>();

    private Dictionary<int, Color> atomColors = new Dictionary<int, Color>
    {
        { 1, Color.white },      // H
        { 6, Color.black },      // C
        { 7, Color.blue },       // N
        { 8, Color.red },        // O
        { 16, Color.yellow },    // S
        { 17, new Color(0, 0.8f, 0) }, // Cl
        { 9, Color.cyan },       // F
        { 35, new Color(0.5f, 0, 0) }, // Br
        { 53, new Color(0.5f, 0, 0.5f) } // I
    };

    void Start()
    {
        // 启动时不自动加载，通过按钮触发
    }

    public void LoadMolecule(string identifier)
    {
        string trimmedId = identifier.Trim();
        string url;

        if (uint.TryParse(trimmedId, out _))
        {
            url = $"https://pubchem.ncbi.nlm.nih.gov/rest/pug/compound/cid/{trimmedId}/SDF?record_type=3d";
            Debug.Log($"正在通过 CID {trimmedId} 请求 SDF...");
        }
        else
        {
            if (trimmedId.Equals("ethanol", StringComparison.OrdinalIgnoreCase))
            {
                Debug.Log("检测到 'ethanol'，将使用 CID 702 以确保获取 3D 数据。");
                url = $"https://pubchem.ncbi.nlm.nih.gov/rest/pug/compound/cid/702/SDF?record_type=3d";
            }
            else if (trimmedId.Equals("water", StringComparison.OrdinalIgnoreCase))
            {
                Debug.Log("检测到 'water'，使用 CID 962 加载水分子");
                url = $"https://pubchem.ncbi.nlm.nih.gov/rest/pug/compound/cid/962/SDF?record_type=3d";
            }
            else
            {
                string encodedName = UnityWebRequest.EscapeURL(trimmedId.ToLower());
                url = $"https://pubchem.ncbi.nlm.nih.gov/rest/pug/compound/name/{encodedName}/SDF?record_type=3d";
                Debug.Log($"正在通过名称 '{trimmedId}' 请求 SDF...");
            }
        }

        StartCoroutine(DownloadAndParseSdf(url));
    }

    IEnumerator DownloadAndParseSdf(string url)
    {
        Debug.Log("请求 URL: " + url);

        // 清除旧模型
        if (container != null)
        {
            foreach (Transform child in container)
                Destroy(child.gameObject);
        }
        atoms.Clear();
        bonds.Clear();

        using (UnityWebRequest www = UnityWebRequest.Get(url))
        {
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"下载失败 (HTTP {www.responseCode}): {www.error}");
                string responseText = www.downloadHandler.text;
                Debug.Log("服务器返回内容 (前 200 字符): " + (responseText.Length > 200 ? responseText.Substring(0, 200) : responseText));
                yield break;
            }

            string response = www.downloadHandler.text;

            if (string.IsNullOrWhiteSpace(response))
            {
                Debug.LogError("收到空响应。");
                yield break;
            }

            if (response.TrimStart().StartsWith("<"))
            {
                Debug.LogError("收到错误响应（非SDF数据）: " +
                    (response.Length > 200 ? response.Substring(0, 200) + "..." : response));
                yield break;
            }

            Debug.Log("开始解析 SDF 数据...");
            if (ParseSdf(response))
            {
                Debug.Log("SDF 解析成功，开始构建分子模型...");
                BuildMolecule();
            }
            else
            {
                Debug.LogError("SDF 文件解析失败。");
            }
        }
    }

    bool ParseSdf(string sdf)
    {
        try
        {
            // 按行拆分，保留空行以便准确定位
            string[] lines = sdf.Split(new[] { '\n', '\r' }, StringSplitOptions.None);
            List<string> nonEmptyLines = new List<string>();

            // 过滤空行但保留原始行号信息
            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    nonEmptyLines.Add(line.TrimEnd());
                }
            }

            if (nonEmptyLines.Count < 5)
            {
                Debug.LogError("SDF 数据行数不足，可能不是有效的 SDF 文件。");
                return false;
            }

            // 解析计数行（根据提供的SDF文件，计数行格式为"  3  2  0     0  0  0  0  0  0999 V2000"）
            int numAtoms = 0;
            int numBonds = 0;
            string countLine = nonEmptyLines[2]; // 水分子SDF中计数行在第3行（0-based index 2）

            // 提取原子数和键数（前两个数字）
            string[] countParts = countLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (countParts.Length >= 2)
            {
                int.TryParse(countParts[0], out numAtoms);
                int.TryParse(countParts[1], out numBonds);
            }

            Debug.Log($"从SDF头部解析到：原子数={numAtoms}，键数={numBonds}");

            // 原子数据从计数行的下一行开始（index 3）
            int atomStartIndex = 3;

            // 解析原子
            atoms.Clear();
            for (int i = 0; i < numAtoms && (atomStartIndex + i) < nonEmptyLines.Count; i++)
            {
                string line = nonEmptyLines[atomStartIndex + i];

                // 原子行格式示例："    0.0000    0.0000    0.0000 O   0  0  0  0  0  0  0  0  0  0  0  0"
                string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length >= 4)
                {
                    if (float.TryParse(parts[0], out float x) &&
                        float.TryParse(parts[1], out float y) &&
                        float.TryParse(parts[2], out float z))
                    {
                        string element = parts[3];
                        int atomicNum = element switch
                        {
                            "H" => 1,
                            "C" => 6,
                            "O" => 8,
                            "N" => 7,
                            "S" => 16,
                            "Cl" => 17,
                            "F" => 9,
                            "Br" => 35,
                            "I" => 53,
                            _ => 0
                        };

                        if (atomicNum != 0)
                        {
                            atoms.Add(new Atom
                            {
                                atomicNum = atomicNum,
                                position = new Vector3(x, y, z) * 2f // 缩放坐标使模型更清晰
                            });
                        }
                        else
                        {
                            Debug.LogWarning($"未知元素: {element}");
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"无法解析原子坐标: {line}");
                    }
                }
                else
                {
                    Debug.LogWarning($"原子行格式不正确: {line}");
                }
            }

            // 键数据从原子数据之后开始
            int bondStartIndex = atomStartIndex + numAtoms;

            // 解析键
            bonds.Clear();
            for (int i = 0; i < numBonds && (bondStartIndex + i) < nonEmptyLines.Count; i++)
            {
                string line = nonEmptyLines[bondStartIndex + i];

                // 键行格式示例："  1  2  1  0  0  0  0"
                string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length >= 3)
                {
                    if (int.TryParse(parts[0], out int from) &&
                        int.TryParse(parts[1], out int to) &&
                        int.TryParse(parts[2], out int bondType))
                    {
                        // SDF中原子索引从1开始，转为0-based
                        from -= 1;
                        to -= 1;

                        if (from >= 0 && from < atoms.Count && to >= 0 && to < atoms.Count)
                        {
                            bonds.Add(new Bond
                            {
                                from = from,
                                to = to,
                                bondType = bondType
                            });
                        }
                        else
                        {
                            Debug.LogWarning($"键索引超出范围: {from + 1} -> {to + 1}");
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"无法解析键数据: {line}");
                    }
                }
                else
                {
                    Debug.LogWarning($"键行格式不正确: {line}");
                }
            }

            Debug.Log($"✅ SDF 解析完成：原子{atoms.Count}/{numAtoms}，键{bonds.Count}/{numBonds}");
            return atoms.Count > 0;
        }
        catch (Exception e)
        {
            Debug.LogError("ParseSdf 异常: ");
            Debug.LogException(e);
            return false;
        }
    }

    void BuildMolecule()
    {
        if (atoms == null || bonds == null) return;

        // 创建原子
        foreach (var atom in atoms)
        {
            if (atom == null) continue;
            GameObject go = Instantiate(atomPrefab, atom.position, Quaternion.identity, container);
            if (go != null)
            {
                Renderer renderer = go.GetComponent<Renderer>();
                if (renderer != null)
                {
                    if (!atomColors.TryGetValue(atom.atomicNum, out Color color))
                        color = Color.gray;
                    renderer.material.color = color;
                }
                atom.gameObject = go;
            }
        }

        // 创建键
        foreach (var bond in bonds)
        {
            if (bond.from < 0 || bond.from >= atoms.Count || bond.to < 0 || bond.to >= atoms.Count)
            {
                Debug.LogWarning($"跳过无效键索引: {bond.from}->{bond.to}");
                continue;
            }
            Vector3 start = atoms[bond.from].position;
            Vector3 end = atoms[bond.to].position;

            GameObject prefab = bondSinglePrefab;
            if (bond.bondType == 2) prefab = bondDoublePrefab;
            else if (bond.bondType == 3) prefab = bondTriplePrefab ?? bondSinglePrefab;

            if (prefab != null)
            {
                CreateBond(start, end, prefab);
            }
        }
    }

    void CreateBond(Vector3 start, Vector3 end, GameObject prefab)
    {
        Vector3 center = (start + end) * 0.5f;
        Vector3 direction = end - start;
        float distance = direction.magnitude;

        if (distance < 0.01f)
        {
            Debug.LogWarning("键长度过短，跳过创建。");
            return;
        }

        Quaternion rot = Quaternion.LookRotation(direction);
        GameObject bond = Instantiate(prefab, center, rot, container);
        if (bond != null)
        {
            // 沿Z轴缩放（匹配LookRotation的朝向）
            bond.transform.localScale = new Vector3(0.1f, 0.1f, distance);
        }
    }

    public void OnSearchClick()
    {
        if (inputField != null)
        {
            string identifier = inputField.text.Trim();
            if (!string.IsNullOrEmpty(identifier))
            {
                LoadMolecule(identifier);
            }
            else
            {
                Debug.LogWarning("输入为空！加载默认分子 CID 702 (乙醇)。");
                LoadMolecule("702");
            }
        }
        else
        {
            Debug.LogError("未设置 inputField！");
        }
    }
}