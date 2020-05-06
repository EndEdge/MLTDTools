﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AssetStudio.Extended.CompositeModels;
using JetBrains.Annotations;
using OpenMLTD.MillionDance.Entities.Internal;
using OpenMLTD.MillionDance.Entities.Mltd.Sway;
using OpenMLTD.MillionDance.Entities.Pmx;
using OpenMLTD.MillionDance.Entities.Pmx.Extensions;
using OpenMLTD.MillionDance.Extensions;
using OpenMLTD.MillionDance.Utilities;
using OpenTK;

namespace OpenMLTD.MillionDance.Core {
    public sealed partial class PmxCreator {

        public PmxModel CreateFrom([NotNull] CompositeAvatar combinedAvatar, [NotNull] CompositeMesh combinedMesh, int bodyMeshVertexCount, [NotNull] string texturePrefix,
            [NotNull] SwayController bodySway, [NotNull] SwayController headSway) {
            var model = new PmxModel();

            model.Name = "ミリシタ モデル00";
            model.NameEnglish = "MODEL_00";
            model.Comment = "製作：MillionDance" + Environment.NewLine + "©BANDAI NAMCO Entertainment Inc.";
            model.CommentEnglish = "Generated by MillionDance" + Environment.NewLine + "©BANDAI NAMCO Entertainment Inc.";

            var vertices = AddVertices(combinedAvatar, combinedMesh, bodyMeshVertexCount);
            model.Vertices = vertices;

            var indicies = AddIndices(combinedMesh);
            model.FaceTriangles = indicies;

            var bones = AddBones(combinedAvatar, combinedMesh, vertices);
            model.Bones = bones;

            if (ConversionConfig.Current.FixTdaBindingPose) {
                if (ConversionConfig.Current.SkeletonFormat == SkeletonFormat.Mmd) {
                    if (ConversionConfig.Current.TranslateBoneNamesToMmd) {
                        FixTdaBonesAndVertices(bones, vertices);
                    }
                } else if (ConversionConfig.Current.SkeletonFormat == SkeletonFormat.Mltd) {
                } else {
                    throw new NotSupportedException("You must choose a motion source to determine skeleton format.");
                }
            }

            var materials = AddMaterials(combinedMesh, texturePrefix);
            model.Materials = materials;

            var emotionMorphs = AddEmotionMorphs(combinedMesh);
            model.Morphs = emotionMorphs;

            // PMX Editor requires at least one node (root), or it will crash because these code:
            /**
             * this.PXRootNode = new PXNode(base.RootNode);
             * this.PXExpressionNode = new PXNode(base.ExpNode);
             * this.PXNode.Clear();
             * this.PXNode.Capacity = base.NodeList.Count - 1; // notice this line
             */
            var nodes = AddNodes(bones, emotionMorphs);
            model.Nodes = nodes;

            if (ConversionConfig.Current.ImportPhysics) {
                (model.RigidBodies, model.Joints) = Physics.ImportPhysics(bones, bodySway, headSway);
            }

            return model;
        }

        [NotNull, ItemNotNull]
        private static IReadOnlyList<PmxVertex> AddVertices([NotNull] CompositeAvatar combinedAvatar, [NotNull] CompositeMesh combinedMesh, int bodyMeshVertexCount) {
            var vertexCount = combinedMesh.VertexCount;
            var vertices = new PmxVertex[vertexCount];
            // In case that vertex count is more than skin count (ill-formed MLTD models: ch_ex005_022ser)
            var skinCount = combinedMesh.Skin.Length;

            for (var i = 0; i < vertexCount; ++i) {
                var vertex = new PmxVertex();

                var position = combinedMesh.Vertices[i];
                var normal = combinedMesh.Normals[i];
                var uv = combinedMesh.UV1[i];

                vertex.Position = position.ToOpenTK().FixUnityToOpenTK();

                if (ConversionConfig.Current.ScaleToPmxSize) {
                    vertex.Position = vertex.Position * ScalingConfig.ScaleUnityToPmx;
                }

                vertex.Normal = normal.ToOpenTK().FixUnityToOpenTK();

                Vector2 fixedUv;

                // Body, then head.
                // TODO: For heads, inverting/flipping is different among models?
                // e.g. ss001_015siz can be processed via the method below; gs001_201xxx's head UVs are not inverted but some are flipped.
                if (i < bodyMeshVertexCount) {
                    // Invert UV!
                    fixedUv = new Vector2(uv.X, 1 - uv.Y);
                } else {
                    fixedUv = uv.ToOpenTK();
                }

                vertex.UV = fixedUv;

                vertex.EdgeScale = 1.0f;

                var skin = i < skinCount ? combinedMesh.Skin[i] : null;
                var affectiveInfluenceCount = skin?.Count(influence => influence != null) ?? 0;

                switch (affectiveInfluenceCount) {
                    case 0:
                        // This vertex is static. It is not attached to any bone.
                        break;
                    case 1:
                        vertex.Deformation = Deformation.Bdef1;
                        break;
                    case 2:
                        vertex.Deformation = Deformation.Bdef2;
                        break;
                    case 3:
                        throw new NotSupportedException($"Not supported: vertex #{i} has 3 influences.");
                    case 4:
                        vertex.Deformation = Deformation.Bdef4;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(affectiveInfluenceCount), "Unsupported number of bones.");
                }

                Debug.Assert(skin != null || affectiveInfluenceCount == 0);

                for (var j = 0; j < affectiveInfluenceCount; ++j) {
                    Debug.Assert(skin != null);

                    var boneId = combinedMesh.BoneNameHashes[skin[j].BoneIndex];
                    var realBoneIndex = combinedAvatar.AvatarSkeleton.NodeIDs.FindIndex(boneId);

                    if (realBoneIndex < 0) {
                        throw new ArgumentOutOfRangeException(nameof(realBoneIndex));
                    }

                    vertex.BoneWeights[j].BoneIndex = realBoneIndex;
                    vertex.BoneWeights[j].Weight = skin[j].Weight;
                }

                vertices[i] = vertex;
            }

            return vertices;
        }

        [NotNull]
        private static IReadOnlyList<int> AddIndices([NotNull] CompositeMesh combinedMesh) {
            var indicies = new int[combinedMesh.Indices.Length];

            for (var i = 0; i < indicies.Length; ++i) {
                indicies[i] = unchecked((int)combinedMesh.Indices[i]);
            }

            return indicies;
        }

        [NotNull, ItemNotNull]
        private static IReadOnlyList<PmxBone> AddBones([NotNull] CompositeAvatar combinedAvatar, [NotNull] CompositeMesh combinedMesh, [NotNull, ItemNotNull] IReadOnlyList<PmxVertex> vertices) {
            var boneCount = combinedAvatar.AvatarSkeleton.NodeIDs.Length;
            var bones = new List<PmxBone>(boneCount);

            var hierachy = BoneUtils.BuildBoneHierarchy(combinedAvatar);

            for (var i = 0; i < boneCount; ++i) {
                var bone = new PmxBone();
                var transform = combinedAvatar.AvatarSkeletonPose.Transforms[i];
                var boneNode = hierachy[i];

                var pmxBoneName = BoneUtils.GetPmxBoneName(boneNode.Path);

                bone.Name = pmxBoneName;
                bone.NameEnglish = BoneUtils.TranslateBoneName(pmxBoneName);

                // PMX's bone positions are in world coordinate system.
                // Unity's are in local coords.
                bone.InitialPosition = boneNode.InitialPositionWorld;
                bone.CurrentPosition = bone.InitialPosition;

                bone.ParentIndex = boneNode.Parent?.Index ?? -1;
                bone.BoneIndex = i;

                var singleDirectChild = GetDirectSingleChildOf(boneNode);

                if (singleDirectChild != null) {
                    bone.SetFlag(BoneFlags.ToBone);
                    bone.To_Bone = singleDirectChild.Index;
                } else {
                    // TODO: Fix this; it should point to a world position.
                    bone.To_Offset = transform.Translation.ToOpenTK().FixUnityToOpenTK();
                }

                // No use. This is just a flag to specify more details to rotation/translation limitation.
                //bone.SetFlag(BoneFlags.LocalFrame);
                bone.InitialRotation = transform.Rotation.ToOpenTK().FixUnityToOpenTK();
                bone.CurrentRotation = bone.InitialRotation;

                //bone.Level = boneNode.Level;
                bone.Level = 0;

                if (BoneUtils.IsBoneMovable(boneNode.Path)) {
                    bone.SetFlag(BoneFlags.Rotation | BoneFlags.Translation);
                } else {
                    bone.SetFlag(BoneFlags.Rotation);
                }

                if (ConversionConfig.Current.HideUnityGeneratedBones) {
                    if (BoneUtils.IsNameGenerated(boneNode.Path)) {
                        bone.ClearFlag(BoneFlags.Visible);
                    }
                }

                bones.Add(bone);
            }

            if (ConversionConfig.Current.FixMmdCenterBones) {
                // Add master (全ての親) and center (センター), recompute bone hierarchy.
                PmxBone master = new PmxBone(), center = new PmxBone();

                master.Name = "全ての親";
                master.NameEnglish = "master";
                center.Name = "センター";
                center.NameEnglish = "center";

                master.ParentIndex = 0; // "" bone
                center.ParentIndex = 1; // "master" bone

                master.CurrentPosition = master.InitialPosition = Vector3.Zero;
                center.CurrentPosition = center.InitialPosition = Vector3.Zero;

                master.SetFlag(BoneFlags.Translation | BoneFlags.Rotation);
                center.SetFlag(BoneFlags.Translation | BoneFlags.Rotation);

                bones.Insert(1, master);
                bones.Insert(2, center);

                //// Fix "MODEL_00" bone

                //do {
                //    var model00 = bones.Find(b => b.Name == "グルーブ");

                //    if (model00 == null) {
                //        throw new ArgumentException("MODEL_00 mapped bone is not found.");
                //    }

                //    model00.ParentIndex = 2; // "center" bone
                //} while (false);

                const int numBonesAdded = 2;

                // Fix vertices and other bones
                foreach (var vertex in vertices) {
                    foreach (var boneWeight in vertex.BoneWeights) {
                        if (boneWeight.BoneIndex == 0 && boneWeight.Weight <= 0) {
                            continue;
                        }

                        if (boneWeight.BoneIndex >= 1) {
                            boneWeight.BoneIndex += numBonesAdded;
                        }
                    }
                }

                for (var i = numBonesAdded + 1; i < bones.Count; ++i) {
                    var bone = bones[i];

                    bone.ParentIndex += numBonesAdded;

                    if (bone.HasFlag(BoneFlags.ToBone)) {
                        bone.To_Bone += numBonesAdded;
                    }
                }
            }

            if (ConversionConfig.Current.AppendIKBones) {
                // Add IK bones. 

                PmxBone[] CreateLegIK(string leftRightJp, string leftRightEn) {
                    var startBoneCount = bones.Count;

                    PmxBone ikParent = new PmxBone(), ikBone = new PmxBone();

                    ikParent.Name = leftRightJp + "足IK親";
                    ikParent.NameEnglish = "leg IKP_" + leftRightEn;
                    ikBone.Name = leftRightJp + "足ＩＫ";
                    ikBone.NameEnglish = "leg IK_" + leftRightEn;

                    PmxBone master;

                    do {
                        master = bones.Find(b => b.Name == "全ての親");

                        if (master == null) {
                            throw new ArgumentException("Missing master bone.");
                        }
                    } while (false);

                    ikParent.ParentIndex = bones.IndexOf(master);
                    ikBone.ParentIndex = startBoneCount; // IKP
                    ikParent.SetFlag(BoneFlags.ToBone);
                    ikBone.SetFlag(BoneFlags.ToBone);
                    ikParent.To_Bone = startBoneCount + 1; // IK
                    ikBone.To_Bone = -1;

                    PmxBone ankle, knee, leg;

                    do {
                        var ankleName = leftRightJp + "足首";
                        ankle = bones.Find(b => b.Name == ankleName);
                        var kneeName = leftRightJp + "ひざ";
                        knee = bones.Find(b => b.Name == kneeName);
                        var legName = leftRightJp + "足";
                        leg = bones.Find(b => b.Name == legName);

                        if (ankle == null) {
                            throw new ArgumentException("Missing ankle bone.");
                        }

                        if (knee == null) {
                            throw new ArgumentException("Missing knee bone.");
                        }

                        if (leg == null) {
                            throw new ArgumentException("Missing leg bone.");
                        }
                    } while (false);

                    ikBone.CurrentPosition = ikBone.InitialPosition = ankle.InitialPosition;
                    ikParent.CurrentPosition = ikParent.InitialPosition = new Vector3(ikBone.InitialPosition.X, 0, ikBone.InitialPosition.Z);

                    ikParent.SetFlag(BoneFlags.Translation | BoneFlags.Rotation);
                    ikBone.SetFlag(BoneFlags.Translation | BoneFlags.Rotation | BoneFlags.IK);

                    var ik = new PmxIK();

                    ik.LoopCount = 10;
                    ik.AngleLimit = MathHelper.DegreesToRadians(114.5916f);
                    ik.TargetBoneIndex = bones.IndexOf(ankle);

                    var links = new IKLink[2];

                    links[0] = new IKLink();
                    links[0].BoneIndex = bones.IndexOf(knee);
                    links[0].IsLimited = true;
                    links[0].LowerBound = new Vector3(MathHelper.DegreesToRadians(-180), 0, 0);
                    links[0].UpperBound = new Vector3(MathHelper.DegreesToRadians(-0.5f), 0, 0);
                    links[1] = new IKLink();
                    links[1].BoneIndex = bones.IndexOf(leg);

                    ik.Links = links;
                    ikBone.IK = ik;

                    return new[] {
                        ikParent, ikBone
                    };
                }

                PmxBone[] CreateToeIK(string leftRightJp, string leftRightEn) {
                    PmxBone ikParent, ikBone = new PmxBone();

                    do {
                        var parentName = leftRightJp + "足ＩＫ";

                        ikParent = bones.Find(b => b.Name == parentName);

                        Debug.Assert(ikParent != null, nameof(ikParent) + " != null");
                    } while (false);

                    ikBone.Name = leftRightJp + "つま先ＩＫ";
                    ikBone.NameEnglish = "toe IK_" + leftRightEn;

                    ikBone.ParentIndex = bones.IndexOf(ikParent);

                    ikBone.SetFlag(BoneFlags.ToBone);
                    ikBone.To_Bone = -1;

                    PmxBone toe, ankle;

                    do {
                        var toeName = leftRightJp + "つま先";
                        toe = bones.Find(b => b.Name == toeName);
                        var ankleName = leftRightJp + "足首";
                        ankle = bones.Find(b => b.Name == ankleName);

                        if (toe == null) {
                            throw new ArgumentException("Missing toe bone.");
                        }

                        if (ankle == null) {
                            throw new ArgumentException("Missing ankle bone.");
                        }
                    } while (false);

                    ikBone.CurrentPosition = ikBone.InitialPosition = toe.InitialPosition;
                    ikBone.SetFlag(BoneFlags.Translation | BoneFlags.Rotation | BoneFlags.IK);

                    var ik = new PmxIK();

                    ik.LoopCount = 10;
                    ik.AngleLimit = MathHelper.DegreesToRadians(114.5916f);
                    ik.TargetBoneIndex = bones.IndexOf(toe);

                    var links = new IKLink[1];

                    links[0] = new IKLink();
                    links[0].BoneIndex = bones.IndexOf(ankle);

                    ik.Links = links.ToArray();
                    ikBone.IK = ik;

                    return new[] {
                        ikBone
                    };
                }

                var leftLegIK = CreateLegIK("左", "L");
                bones.AddRange(leftLegIK);
                var rightLegIK = CreateLegIK("右", "R");
                bones.AddRange(rightLegIK);

                var leftToeIK = CreateToeIK("左", "L");
                bones.AddRange(leftToeIK);
                var rightToeIK = CreateToeIK("右", "R");
                bones.AddRange(rightToeIK);
            }

            if (ConversionConfig.Current.AppendEyeBones) {
                (int VertexStart1, int VertexCount1, int VertexStart2, int VertexCount2) FindEyesVerticeRange() {
                    var meshNameIndex = -1;
                    var cm = combinedMesh as CompositeMesh;

                    Debug.Assert(cm != null, nameof(cm) + " != null");

                    for (var i = 0; i < cm.Names.Count; i++) {
                        var meshName = cm.Names[i];

                        if (meshName == "eyes") {
                            meshNameIndex = i;
                            break;
                        }
                    }

                    if (meshNameIndex < 0) {
                        throw new ArgumentException("Mesh \"eyes\" is missing.");
                    }

                    var subMeshMaps = cm.ParentMeshIndices.Enumerate().Where(s => s.Value == meshNameIndex).ToArray();

                    Debug.Assert(subMeshMaps.Length == 2, "There should be 2 sub mesh maps.");
                    Debug.Assert(subMeshMaps[1].Index - subMeshMaps[0].Index == 1, "The first sub mesh map should contain one element.");

                    var vertexStart1 = (int)cm.SubMeshes[subMeshMaps[0].Index].FirstVertex;
                    var vertexCount1 = (int)cm.SubMeshes[subMeshMaps[0].Index].VertexCount;
                    var vertexStart2 = (int)cm.SubMeshes[subMeshMaps[1].Index].FirstVertex;
                    var vertexCount2 = (int)cm.SubMeshes[subMeshMaps[1].Index].VertexCount;

                    return (vertexStart1, vertexCount1, vertexStart2, vertexCount2);
                }

                Vector3 GetEyeBonePosition(int vertexStart, int vertexCount) {
                    var centerPos = Vector3.Zero;
                    var leftMostPos = new Vector3(float.MinValue, 0, 0);
                    var rightMostPos = new Vector3(float.MaxValue, 0, 0);
                    int leftMostIndex = -1, rightMostIndex = -1;

                    for (var i = vertexStart; i < vertexStart + vertexCount; ++i) {
                        var pos = vertices[i].Position;

                        centerPos += pos;

                        if (pos.X > leftMostPos.X) {
                            leftMostPos = pos;
                            leftMostIndex = i;
                        }

                        if (pos.X < rightMostPos.X) {
                            rightMostPos = pos;
                            rightMostIndex = i;
                        }
                    }

                    Debug.Assert(leftMostIndex >= 0, nameof(leftMostIndex) + " >= 0");
                    Debug.Assert(rightMostIndex >= 0, nameof(rightMostIndex) + " >= 0");

                    centerPos = centerPos / vertexCount;

                    // "Eyeball". You got the idea?
                    var leftMostNorm = vertices[leftMostIndex].Normal;
                    var rightMostNorm = vertices[rightMostIndex].Normal;

                    var k1 = leftMostNorm.Z / leftMostNorm.X;
                    var k2 = rightMostNorm.Z / rightMostNorm.X;
                    float x1 = leftMostPos.X, x2 = rightMostPos.X, z1 = leftMostPos.Z, z2 = rightMostPos.Z;

                    var d1 = (z2 - k2 * x2 + k2 * x1 - z1) / (k1 - k2);

                    var x = x1 + d1;
                    var z = z1 + k1 * d1;

                    return new Vector3(x, centerPos.Y, z);
                }

                Vector3 GetEyesBonePosition(int vertexStart1, int vertexCount1, int vertexStart2, int vertexCount2) {
                    var result = new Vector3();

                    for (var i = vertexStart1; i < vertexStart1 + vertexCount1; ++i) {
                        result += vertices[i].Position;
                    }

                    for (var i = vertexStart2; i < vertexStart2 + vertexCount2; ++i) {
                        result += vertices[i].Position;
                    }

                    result = result / (vertexCount1 + vertexCount2);

                    return new Vector3(0, result.Y + 0.5f, -0.6f);
                }

                var (vs1, vc1, vs2, vc2) = FindEyesVerticeRange();
                PmxBone head;

                do {
                    head = bones.Find(b => b.Name == "頭");

                    if (head == null) {
                        throw new ArgumentException("Missing head bone.");
                    }
                } while (false);

                var eyes = new PmxBone();

                eyes.Name = "両目";
                eyes.NameEnglish = "eyes";

                eyes.Parent = head;
                eyes.ParentIndex = bones.IndexOf(head);

                eyes.CurrentPosition = eyes.InitialPosition = GetEyesBonePosition(vs1, vc1, vs2, vc2);

                eyes.SetFlag(BoneFlags.Visible | BoneFlags.Rotation | BoneFlags.ToBone);
                eyes.To_Bone = -1;

                bones.Add(eyes);

                PmxBone leftEye = new PmxBone(), rightEye = new PmxBone();

                leftEye.Name = "左目";
                leftEye.NameEnglish = "eye_L";
                rightEye.Name = "右目";
                rightEye.NameEnglish = "eye_R";

                leftEye.Parent = head;
                leftEye.ParentIndex = bones.IndexOf(head);
                rightEye.Parent = head;
                rightEye.ParentIndex = bones.IndexOf(head);

                leftEye.SetFlag(BoneFlags.Visible | BoneFlags.Rotation | BoneFlags.ToBone | BoneFlags.AppendRotation);
                rightEye.SetFlag(BoneFlags.Visible | BoneFlags.Rotation | BoneFlags.ToBone | BoneFlags.AppendRotation);
                leftEye.To_Bone = -1;
                rightEye.To_Bone = -1;
                leftEye.AppendParent = eyes;
                rightEye.AppendParent = eyes;
                leftEye.AppendParentIndex = bones.IndexOf(eyes);
                rightEye.AppendParentIndex = bones.IndexOf(eyes);
                leftEye.AppendRatio = 1;
                rightEye.AppendRatio = 1;

                leftEye.CurrentPosition = leftEye.InitialPosition = GetEyeBonePosition(vs1, vc1);
                rightEye.CurrentPosition = rightEye.InitialPosition = GetEyeBonePosition(vs2, vc2);

                bones.Add(leftEye);
                bones.Add(rightEye);

                // Fix vertices
                {
                    var leftEyeIndex = bones.IndexOf(leftEye);
                    var rightEyeIndex = bones.IndexOf(rightEye);

                    for (var i = vs1; i < vs1 + vc1; ++i) {
                        var skin = vertices[i];
                        // Eyes are only affected by "KUBI/ATAMA" bone by default. So we only need to set one element's values.
                        skin.BoneWeights[0].BoneIndex = leftEyeIndex;
                        Debug.Assert(Math.Abs(skin.BoneWeights[0].Weight - 1) < 0.000001f, "Total weight in the skin of left eye should be 1.");
                    }

                    for (var i = vs2; i < vs2 + vc2; ++i) {
                        var skin = vertices[i];
                        // Eyes are only affected by "KUBI/ATAMA" bone by default. So we only need to set one element's values.
                        skin.BoneWeights[0].BoneIndex = rightEyeIndex;
                        Debug.Assert(Math.Abs(skin.BoneWeights[0].Weight - 1) < 0.000001f, "Total weight in the skin of right eye should be 1.");
                    }
                }
            }

            // Finally, set the indices. The values will be used later.
            for (var i = 0; i < bones.Count; i++) {
                bones[i].BoneIndex = i;
            }

            return bones.ToArray();
        }

        // Change standard T-pose to TDA T-pose
        private static void FixTdaBonesAndVertices([NotNull, ItemNotNull] IReadOnlyList<PmxBone> bones, [NotNull, ItemNotNull] IReadOnlyList<PmxVertex> vertices) {
            var defRotRight = Quaternion.FromEulerAngles(0, 0, MathHelper.DegreesToRadians(34.5f));
            var defRotLeft = Quaternion.FromEulerAngles(0, 0, MathHelper.DegreesToRadians(-34.5f));

            var leftArm = bones.SingleOrDefault(b => b.Name == "左腕");
            var rightArm = bones.SingleOrDefault(b => b.Name == "右腕");

            Debug.Assert(leftArm != null, nameof(leftArm) + " != null");
            Debug.Assert(rightArm != null, nameof(rightArm) + " != null");

            leftArm.AnimatedRotation = defRotLeft;
            rightArm.AnimatedRotation = defRotRight;

            foreach (var bone in bones) {
                if (bone.ParentIndex >= 0) {
                    bone.Parent = bones[bone.ParentIndex];
                }
            }

            foreach (var bone in bones) {
                bone.SetToVmdPose(true);
            }

            foreach (var vertex in vertices) {
                var m = Matrix4.Zero;

                for (var j = 0; j < 4; ++j) {
                    var boneWeight = vertex.BoneWeights[j];

                    if (!boneWeight.IsValid) {
                        continue;
                    }

                    m = m + bones[boneWeight.BoneIndex].SkinMatrix * boneWeight.Weight;
                }

                vertex.Position = Vector3.TransformPosition(vertex.Position, m);
                vertex.Normal = Vector3.TransformNormal(vertex.Normal, m);
            }

            foreach (var bone in bones) {
                bone.InitialPosition = bone.CurrentPosition;
            }
        }

        [NotNull, ItemNotNull]
        private static IReadOnlyList<PmxMaterial> AddMaterials([NotNull] PrettyMesh combinedMesh, [NotNull] string texturePrefix) {
            var materialCount = combinedMesh.SubMeshes.Length;
            var materials = new PmxMaterial[materialCount];

            for (var i = 0; i < materialCount; ++i) {
                var material = new PmxMaterial();

                material.NameEnglish = material.Name = $"Mat #{i:00}";
                material.AppliedFaceVertexCount = (int)combinedMesh.SubMeshes[i].IndexCount;
                material.Ambient = new Vector3(0.5f, 0.5f, 0.5f);
                material.Diffuse = Vector4.One;
                material.Specular = Vector3.Zero;
                material.EdgeColor = new Vector4(0.3f, 0.3f, 0.3f, 0.8f);
                material.EdgeSize = 1.0f;
                // TODO: The right way: reading textures' path ID and do the mapping.
                material.TextureFileName = $"{texturePrefix}{i:00}.png";

                material.Flags = MaterialFlags.Shadow | MaterialFlags.SelfShadow | MaterialFlags.SelfShadowMap | MaterialFlags.CullNone | MaterialFlags.Edge;

                materials[i] = material;
            }

            return materials;
        }

        [NotNull, ItemNotNull]
        private static IReadOnlyList<PmxMorph> AddEmotionMorphs([NotNull] PrettyMesh mesh) {
            var morphs = new List<PmxMorph>();

            var s = mesh.Shape;

            if (s != null) {
                Debug.Assert(s.Channels.Length == s.Shapes.Length, "s.Channels.Count == s.Shapes.Count");
                Debug.Assert(s.Channels.Length == s.FullWeights.Length, "s.Channels.Count == s.FullWeights.Count");

                var morphCount = s.Channels.Length;

                for (var i = 0; i < morphCount; i++) {
                    var channel = s.Channels[i];
                    var shape = s.Shapes[i];
                    var vertices = s.Vertices;
                    var morph = new PmxMorph();

                    var morphName = channel.Name.Substring(12); // - "blendShape1."

                    if (ConversionConfig.Current.TranslateFacialExpressionNamesToMmd) {
                        morph.Name = MorphUtils.LookupMorphName(morphName);
                    } else {
                        morph.Name = morphName;
                    }

                    morph.NameEnglish = morphName;

                    morph.OffsetKind = MorphOffsetKind.Vertex;

                    var offsets = new List<PmxBaseMorph>();

                    for (var j = shape.FirstVertex; j < shape.FirstVertex + shape.VertexCount; ++j) {
                        var v = vertices[(int)j];
                        var m = new PmxVertexMorph();

                        var offset = v.Vertex.ToOpenTK().FixUnityToOpenTK();

                        if (ConversionConfig.Current.ScaleToPmxSize) {
                            offset = offset * ScalingConfig.ScaleUnityToPmx;
                        }

                        m.Index = (int)v.Index;
                        m.Offset = offset;

                        offsets.Add(m);
                    }

                    morph.Offsets = offsets.ToArray();

                    morphs.Add(morph);
                }

                // Now some custom morphs for our model to be compatible with TDA.
                do {
                    PmxMorph CreateCompositeMorph(string mltdTruncMorphName, params string[] truncNames) {
                        int FindIndex<T>(IReadOnlyList<T> list, T item) {
                            var comparer = EqualityComparer<T>.Default;

                            for (var i = 0; i < list.Count; ++i) {
                                if (comparer.Equals(item, list[i])) {
                                    return i;
                                }
                            }

                            return -1;
                        }

                        var morph = new PmxMorph();

                        if (ConversionConfig.Current.TranslateFacialExpressionNamesToMmd) {
                            morph.Name = MorphUtils.LookupMorphName(mltdTruncMorphName);
                        } else {
                            morph.Name = mltdTruncMorphName;
                        }

                        morph.NameEnglish = mltdTruncMorphName;

                        var offsets = new List<PmxBaseMorph>();
                        var vertices = s.Vertices;

                        var matchedChannels = truncNames.Select(name => {
                            // name: e.g. "E_metoji_l"
                            // ch_ex005_016tsu has "blendShape2.E_metoji_l" instead of the common one "blendShape1.E_metoji_l"
                            // so the old method (string equal to full name) breaks.
                            var chan = s.Channels.SingleOrDefault(ch => ch.Name.EndsWith(name));

                            if (chan == null) {
                                Trace.WriteLine($"Warning: required blend channel not found: {name}");
                            }

                            return chan;
                        }).ToArray();

                        foreach (var channel in matchedChannels) {
                            if (channel == null) {
                                continue;
                            }

                            var channelIndex = FindIndex(s.Channels, channel);
                            var shape = s.Shapes[channelIndex];

                            morph.OffsetKind = MorphOffsetKind.Vertex;

                            for (var j = shape.FirstVertex; j < shape.FirstVertex + shape.VertexCount; ++j) {
                                var v = vertices[(int)j];
                                var m = new PmxVertexMorph();

                                var offset = v.Vertex.ToOpenTK().FixUnityToOpenTK();

                                if (ConversionConfig.Current.ScaleToPmxSize) {
                                    offset = offset * ScalingConfig.ScaleUnityToPmx;
                                }

                                m.Index = (int)v.Index;
                                m.Offset = offset;

                                offsets.Add(m);
                            }
                        }

                        morph.Offsets = offsets.ToArray();

                        return morph;
                    }

                    morphs.Add(CreateCompositeMorph("E_metoji", "E_metoji_l", "E_metoji_r"));
                } while (false);
            }

            return morphs.ToArray();
        }

        [NotNull, ItemNotNull]
        private static IReadOnlyList<PmxNode> AddNodes([NotNull, ItemNotNull] IReadOnlyList<PmxBone> bones, [NotNull, ItemNotNull] IReadOnlyList<PmxMorph> morphs) {
            var nodes = new List<PmxNode>();

            PmxNode CreateBoneGroup(string groupNameJp, string groupNameEn, params string[] boneNames) {
                var node = new PmxNode();

                node.Name = groupNameJp;
                node.NameEnglish = groupNameEn;

                var boneNodes = new List<NodeElement>();

                foreach (var boneName in boneNames) {
                    var bone = bones.SingleOrDefault(b => b.Name == boneName);

                    if (bone != null) {
                        boneNodes.Add(new NodeElement {
                            ElementType = ElementType.Bone,
                            Index = bone.BoneIndex
                        });
                    } else {
                        Debug.Print("Warning: bone node not found: {0}", boneName);
                    }
                }

                node.Elements = boneNodes.ToArray();

                return node;
            }

            PmxNode CreateEmotionNode() {
                var node = new PmxNode();

                node.Name = "表情";
                node.NameEnglish = "Facial Expressions";

                var elements = new List<NodeElement>();

                var counter = 0;

                foreach (var _ in morphs) {
                    var elem = new NodeElement();

                    elem.ElementType = ElementType.Morph;
                    elem.Index = counter;

                    elements.Add(elem);

                    ++counter;
                }

                node.Elements = elements.ToArray();

                return node;
            }

            nodes.Add(CreateBoneGroup("Root", "Root", "操作中心"));
            nodes.Add(CreateEmotionNode());
            nodes.Add(CreateBoneGroup("センター", "center", "全ての親", "センター"));
            nodes.Add(CreateBoneGroup("ＩＫ", "IK", "左足IK親", "左足ＩＫ", "左つま先ＩＫ", "右足IK親", "右足ＩＫ", "右つま先ＩＫ"));
            nodes.Add(CreateBoneGroup("体(上)", "Upper Body", "上半身", "上半身2", "首", "頭"));
            nodes.Add(CreateBoneGroup("腕", "Arms", "左肩", "左腕", "左ひじ", "左手首", "右肩", "右腕", "右ひじ", "右手首"));
            nodes.Add(CreateBoneGroup("手", "Hands", "左親指１", "左親指２", "左親指３", "左人指１", "左人指２", "左人指３", "左ダミー", "左中指１", "左中指２", "左中指３", "左薬指１", "左薬指２", "左薬指３", "左小指１", "左小指２", "左小指３",
                "右親指１", "右親指２", "右親指３", "右人指１", "右人指２", "右人指３", "右ダミー", "右中指１", "右中指２", "右中指３", "右薬指１", "右薬指２", "右薬指３", "右小指１", "右小指２", "右小指３"));
            nodes.Add(CreateBoneGroup("体(下)", "Lower Body", "グルーブ", "腰", "下半身"));
            nodes.Add(CreateBoneGroup("足", "Legs", "左足", "左ひざ", "左足首", "左つま先", "右足", "右ひざ", "右足首", "右つま先"));
            nodes.Add(CreateBoneGroup("その他", "Others", "両目", "左目", "右目"));

            return nodes.ToArray();
        }

        [CanBeNull]
        private static BoneNode GetDirectSingleChildOf([NotNull] BoneNode b) {
            var l = new List<BoneNode>();

            foreach (var c in b.Children) {
                var isGenerated = BoneUtils.IsNameGenerated(c.Path);

                if (!isGenerated) {
                    l.Add(c);
                }
            }

            if (l.Count == 1) {
                return l[0];
            } else {
                return null;
            }
        }

    }
}
