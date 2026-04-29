#if UNITY_EDITOR

using System.Collections.Generic;
using UnityEngine;

namespace Grass.Core
{
	public static class GrassCreator
	{
		private const float g = 1.32471795572f;
		private static readonly Collider[] cullColliders = new Collider[32];

		private static float GetRandomHash(Vector3 worldPosition)
		{
			uint hash = (uint)Mathf.Abs(worldPosition.x * 73856093f) ^
			            (uint)Mathf.Abs(worldPosition.z * 19349663f) ^
			            (uint)Mathf.Abs(worldPosition.y * 97531.0f);
			return (hash % 10000) * 0.0001f;
		}

		private static Vector3 ApplyNormalNudge(Vector3 originalNormal, float maxNudgeAmount, Vector3 worldPosition)
		{
			float nudgeX = (GetRandomHash(worldPosition + Vector3.right) - 0.5f) * 2f * maxNudgeAmount;
			float nudgeZ = (GetRandomHash(worldPosition + Vector3.forward) - 0.5f) * 2f * maxNudgeAmount;

			Quaternion rotationX = Quaternion.AngleAxis(nudgeX * Mathf.Rad2Deg, Vector3.right);
			Quaternion rotationZ = Quaternion.AngleAxis(nudgeZ * Mathf.Rad2Deg, Vector3.forward);
			return (rotationZ * rotationX * originalNormal).normalized;
		}

		private static void AssignGrassMaterial(ref GrassData grassData, GrassMaterialSystem materialSystem)
		{
			if (materialSystem == null || !materialSystem.IsValid())
			{
				grassData.materialIndex = 0;
				return;
			}

			grassData.materialIndex = materialSystem.SelectVariantIndex(grassData.position);
			GrassVariant variant = materialSystem.GetValidVariant(grassData.materialIndex);

			if (variant != null && GetRandomHash(grassData.position) < variant.normalNudgeProbability)
				grassData.normal = ApplyNormalNudge(grassData.normal, variant.normalNudgeStrength, grassData.position);
		}

		public static bool TryGeneratePoints(GrassHolder grassHolder, GameObject target, int totalGrassAmount,
			LayerMask cullMask, float normalLimit, GrassMaterialSystem materialSystem = null)
		{
			if (target.TryGetComponent(out MeshFilter sourceMesh) &&
			    target.TryGetComponent(out MeshRenderer meshRenderer))
			{
				grassHolder._rootMeshMaterial = meshRenderer.sharedMaterial;

				int[] triangles = sourceMesh.sharedMesh.triangles;
				Vector3[] vertices = sourceMesh.sharedMesh.vertices;
				Vector3[] normals = sourceMesh.sharedMesh.normals;
				var lightmapUVs = new List<Vector2>(vertices.Length);
				sourceMesh.sharedMesh.GetUVs(1, lightmapUVs);

				Matrix4x4 localToWorldMatrix = target.transform.localToWorldMatrix;
				Matrix4x4 localToWorldMatrixInverseTranspose = target.transform.localToWorldMatrix.inverse.transpose;

				for (int i = 0; i < normals.Length; i++)
				{
					vertices[i] = localToWorldMatrix * vertices[i];
					normals[i] = localToWorldMatrixInverseTranspose * normals[i];
					normals[i] /= normals[i].magnitude;
				}

				float surfaceAreas = CalculateSurfaceArea(triangles, vertices, out float[] areas);
				var grassData = new GrassData();

				for (int i = 0; i < areas.Length; i++)
				{
					grassData.normal = normals[triangles[i * 3]];

					if (grassData.normal.y < 1 - normalLimit)
						continue;

					int vi1 = triangles[i * 3];
					int vi2 = triangles[i * 3 + 1];
					int vi3 = triangles[i * 3 + 2];

					Vector3 root = vertices[vi1];
					Vector3 v1 = vertices[vi2] - root;
					Vector3 v2 = vertices[vi3] - root;
					int countGrassOnTriangle = (int)(totalGrassAmount * areas[i] / surfaceAreas);

					for (int j = 0; j < countGrassOnTriangle; j++)
					{
						float r1 = (j / g) % 1;
						float r2 = (j / g / g) % 1;
						if (r1 + r2 > 1)
						{
							r1 = 1 - r1;
							r2 = 1 - r2;
						}

						grassData.position = root + r1 * v1 + r2 * v2;
						AssignGrassMaterial(ref grassData, materialSystem);

						if (lightmapUVs.Count != 0)
						{
							Vector2 lmRoot = lightmapUVs[vi1];
							Vector2 lmV1 = lightmapUVs[vi2] - lmRoot;
							Vector2 lmV2 = lightmapUVs[vi3] - lmRoot;
							Vector4 scaleOffset = meshRenderer.lightmapScaleOffset;

							grassData.lightmapUV = lmRoot + r1 * lmV1 + r2 * lmV2;
							grassData.lightmapUV.x = grassData.lightmapUV.x * scaleOffset.x + scaleOffset.z;
							grassData.lightmapUV.y = grassData.lightmapUV.y * scaleOffset.y + scaleOffset.w;
						}

						if (Physics.OverlapBoxNonAlloc(grassData.position, Vector3.one * 0.2f, cullColliders,
							    Quaternion.identity, cullMask) > 0)
							continue;

						grassHolder.grassData.Add(grassData);
					}
				}

				grassHolder.lightmapIndex = meshRenderer.lightmapIndex;
				grassHolder.SetGrassClearedFlag(false);
				grassHolder.FastSetup();
				return true;
			}

			if (target.TryGetComponent(out Terrain terrain))
			{
				grassHolder._rootMeshMaterial = terrain.materialTemplate;

				GrassData grassData = new();
				Vector3 v1 = new Vector3(1, 0, 0) * terrain.terrainData.size.x;
				Vector3 v2 = new Vector3(0, 0, 1) * terrain.terrainData.size.z;
				Vector3 root = terrain.GetPosition();
				int grassCounter = 0;
				int i = 0;
				int maxAttempts = Mathf.Max(totalGrassAmount * 50, 1000);

				while (grassCounter < totalGrassAmount && i < maxAttempts)
				{
					i++;
					float r1 = (i / g) % 1;
					float r2 = (i / g / g) % 1;

					grassData.normal = terrain.terrainData.GetInterpolatedNormal(r1, r2);
					grassData.position = root + r1 * v1 + r2 * v2;
					grassData.position.y = terrain.SampleHeight(grassData.position) + terrain.GetPosition().y;
					// grassData.position -= grassData.normal / 5;

					AssignGrassMaterial(ref grassData, materialSystem);

					Vector4 scaleOffset = terrain.lightmapScaleOffset;
					grassData.lightmapUV = new Vector2(
						r1 * scaleOffset.x + scaleOffset.z,
						r2 * scaleOffset.y + scaleOffset.w);

					if (grassData.normal.y < 1 - normalLimit)
						continue;

					if (Physics.OverlapBoxNonAlloc(grassData.position, Vector3.one * 0.2f, cullColliders,
						    Quaternion.identity, cullMask) > 0)
						continue;

					grassCounter++;
					grassHolder.grassData.Add(grassData);
				}

				if (grassCounter < totalGrassAmount)
				{
					Debug.LogWarning(
						$"GrassCreator: Generated {grassCounter}/{totalGrassAmount} terrain blades before reaching the attempt limit. Check slope tolerance and obstacle mask.",
						grassHolder);
				}

				grassHolder.lightmapIndex = terrain.lightmapIndex;
				grassHolder.SetGrassClearedFlag(false);
				grassHolder.FastSetup();
				return true;
			}

			return false;
		}

		private static float CalculateSurfaceArea(int[] tris, Vector3[] verts, out float[] sizes)
		{
			float result = 0f;
			int triangleCount = tris.Length / 3;
			sizes = new float[triangleCount];

			for (int i = 0; i < triangleCount; i++)
			{
				result += sizes[i] = 0.5f * Vector3.Cross(
					verts[tris[i * 3 + 1]] - verts[tris[i * 3]],
					verts[tris[i * 3 + 2]] - verts[tris[i * 3]]).magnitude;
			}

			return result;
		}
	}
}
#endif
