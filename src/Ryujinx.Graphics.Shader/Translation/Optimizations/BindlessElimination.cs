﻿using Ryujinx.Graphics.Shader.Instructions;
using Ryujinx.Graphics.Shader.IntermediateRepresentation;
using Ryujinx.Graphics.Shader.StructuredIr;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Shader.Translation.Optimizations
{
    class BindlessElimination
    {
        public static void RunPass(BasicBlock block, ResourceManager resourceManager, IGpuAccessor gpuAccessor)
        {
            // We can turn a bindless into regular access by recognizing the pattern
            // produced by the compiler for separate texture and sampler.
            // We check for the following conditions:
            // - The handle is a constant buffer value.
            // - The handle is the result of a bitwise OR logical operation.
            // - Both sources of the OR operation comes from a constant buffer.
            for (LinkedListNode<INode> node = block.Operations.First; node != null; node = node.Next)
            {
                if (node.Value is not TextureOperation texOp)
                {
                    continue;
                }

                if ((texOp.Flags & TextureFlags.Bindless) == 0)
                {
                    continue;
                }

                if (texOp.Inst == Instruction.TextureSample || texOp.Inst.IsTextureQuery())
                {
                    Operand bindlessHandle = Utils.FindLastOperation(texOp.GetSource(0), block);

                    // Some instructions do not encode an accurate sampler type:
                    // - Most instructions uses the same type for 1D and Buffer.
                    // - Query instructions may not have any type.
                    // For those cases, we need to try getting the type from current GPU state,
                    // as long bindless elimination is successful and we know where the texture descriptor is located.
                    bool rewriteSamplerType =
                        texOp.Type == SamplerType.TextureBuffer ||
                        texOp.Inst == Instruction.TextureQuerySamples ||
                        texOp.Inst == Instruction.TextureQuerySize;

                    if (bindlessHandle.Type == OperandType.ConstantBuffer)
                    {
                        SetHandle(
                            resourceManager,
                            gpuAccessor,
                            texOp,
                            bindlessHandle.GetCbufOffset(),
                            bindlessHandle.GetCbufSlot(),
                            rewriteSamplerType,
                            isImage: false);

                        continue;
                    }

                    if (bindlessHandle.AsgOp is not Operation handleCombineOp)
                    {
                        continue;
                    }

                    if (handleCombineOp.Inst != Instruction.BitwiseOr)
                    {
                        continue;
                    }

                    Operand src0 = Utils.FindLastOperation(handleCombineOp.GetSource(0), block);
                    Operand src1 = Utils.FindLastOperation(handleCombineOp.GetSource(1), block);

                    // For cases where we have a constant, ensure that the constant is always
                    // the second operand.
                    // Since this is a commutative operation, both are fine,
                    // and having a "canonical" representation simplifies some checks below.
                    if (src0.Type == OperandType.Constant && src1.Type != OperandType.Constant)
                    {
                        (src0, src1) = (src1, src0);
                    }

                    TextureHandleType handleType = TextureHandleType.SeparateSamplerHandle;

                    // Try to match the following patterns:
                    // Masked pattern:
                    //  - samplerHandle = samplerHandle & 0xFFF00000;
                    //  - textureHandle = textureHandle & 0xFFFFF;
                    //  - combinedHandle = samplerHandle | textureHandle;
                    //  Where samplerHandle and textureHandle comes from a constant buffer.
                    // Shifted pattern:
                    //  - samplerHandle = samplerId << 20;
                    //  - combinedHandle = samplerHandle | textureHandle;
                    //  Where samplerId and textureHandle comes from a constant buffer.
                    // Constant pattern:
                    //  - combinedHandle = samplerHandleConstant | textureHandle;
                    //  Where samplerHandleConstant is a constant value, and textureHandle comes from a constant buffer.
                    if (src0.AsgOp is Operation src0AsgOp)
                    {
                        if (src1.AsgOp is Operation src1AsgOp &&
                            src0AsgOp.Inst == Instruction.BitwiseAnd &&
                            src1AsgOp.Inst == Instruction.BitwiseAnd)
                        {
                            src0 = GetSourceForMaskedHandle(src0AsgOp, 0xFFFFF);
                            src1 = GetSourceForMaskedHandle(src1AsgOp, 0xFFF00000);

                            // The OR operation is commutative, so we can also try to swap the operands to get a match.
                            if (src0 == null || src1 == null)
                            {
                                src0 = GetSourceForMaskedHandle(src1AsgOp, 0xFFFFF);
                                src1 = GetSourceForMaskedHandle(src0AsgOp, 0xFFF00000);
                            }

                            if (src0 == null || src1 == null)
                            {
                                continue;
                            }
                        }
                        else if (src0AsgOp.Inst == Instruction.ShiftLeft)
                        {
                            Operand shift = src0AsgOp.GetSource(1);

                            if (shift.Type == OperandType.Constant && shift.Value == 20)
                            {
                                src0 = src1;
                                src1 = src0AsgOp.GetSource(0);
                                handleType = TextureHandleType.SeparateSamplerId;
                            }
                        }
                    }
                    else if (src1.AsgOp is Operation src1AsgOp && src1AsgOp.Inst == Instruction.ShiftLeft)
                    {
                        Operand shift = src1AsgOp.GetSource(1);

                        if (shift.Type == OperandType.Constant && shift.Value == 20)
                        {
                            src1 = src1AsgOp.GetSource(0);
                            handleType = TextureHandleType.SeparateSamplerId;
                        }
                    }
                    else if (src1.Type == OperandType.Constant && (src1.Value & 0xfffff) == 0)
                    {
                        handleType = TextureHandleType.SeparateConstantSamplerHandle;
                    }

                    if (src0.Type != OperandType.ConstantBuffer)
                    {
                        continue;
                    }

                    if (handleType == TextureHandleType.SeparateConstantSamplerHandle)
                    {
                        SetHandle(
                            resourceManager,
                            gpuAccessor,
                            texOp,
                            TextureHandle.PackOffsets(src0.GetCbufOffset(), ((src1.Value >> 20) & 0xfff), handleType),
                            TextureHandle.PackSlots(src0.GetCbufSlot(), 0),
                            rewriteSamplerType,
                            isImage: false);
                    }
                    else if (src1.Type == OperandType.ConstantBuffer)
                    {
                        SetHandle(
                            resourceManager,
                            gpuAccessor,
                            texOp,
                            TextureHandle.PackOffsets(src0.GetCbufOffset(), src1.GetCbufOffset(), handleType),
                            TextureHandle.PackSlots(src0.GetCbufSlot(), src1.GetCbufSlot()),
                            rewriteSamplerType,
                            isImage: false);
                    }
                }
                else if (texOp.Inst == Instruction.ImageLoad ||
                         texOp.Inst == Instruction.ImageStore ||
                         texOp.Inst == Instruction.ImageAtomic)
                {
                    Operand src0 = Utils.FindLastOperation(texOp.GetSource(0), block);

                    if (src0.Type == OperandType.ConstantBuffer)
                    {
                        int cbufOffset = src0.GetCbufOffset();
                        int cbufSlot = src0.GetCbufSlot();

                        if (texOp.Format == TextureFormat.Unknown)
                        {
                            if (texOp.Inst == Instruction.ImageAtomic)
                            {
                                texOp.Format = ShaderProperties.GetTextureFormatAtomic(gpuAccessor, cbufOffset, cbufSlot);
                            }
                            else
                            {
                                texOp.Format = ShaderProperties.GetTextureFormat(gpuAccessor, cbufOffset, cbufSlot);
                            }
                        }

                        bool rewriteSamplerType = texOp.Type == SamplerType.TextureBuffer;

                        SetHandle(resourceManager, gpuAccessor, texOp, cbufOffset, cbufSlot, rewriteSamplerType, isImage: true);
                    }
                }
            }
        }

        private static Operand GetSourceForMaskedHandle(Operation asgOp, uint mask)
        {
            // Assume it was already checked that the operation is bitwise AND.
            Operand src0 = asgOp.GetSource(0);
            Operand src1 = asgOp.GetSource(1);

            if (src0.Type == OperandType.ConstantBuffer && src1.Type == OperandType.ConstantBuffer)
            {
                // We can't check if the mask matches here as both operands are from a constant buffer.
                // Be optimistic and assume it matches. Avoid constant buffer 1 as official drivers
                // uses this one to store compiler constants.
                return src0.GetCbufSlot() == 1 ? src1 : src0;
            }
            else if (src0.Type == OperandType.ConstantBuffer && src1.Type == OperandType.Constant)
            {
                if ((uint)src1.Value == mask)
                {
                    return src0;
                }
            }
            else if (src0.Type == OperandType.Constant && src1.Type == OperandType.ConstantBuffer)
            {
                if ((uint)src0.Value == mask)
                {
                    return src1;
                }
            }

            return null;
        }

        private static void SetHandle(
            ResourceManager resourceManager,
            IGpuAccessor gpuAccessor,
            TextureOperation texOp,
            int cbufOffset,
            int cbufSlot,
            bool rewriteSamplerType,
            bool isImage)
        {
            if (rewriteSamplerType)
            {
                SamplerType newType = gpuAccessor.QuerySamplerType(cbufOffset, cbufSlot);

                if (texOp.Inst.IsTextureQuery())
                {
                    texOp.Type = newType;
                }
                else if (texOp.Type == SamplerType.TextureBuffer && newType == SamplerType.Texture1D)
                {
                    int coordsCount = 2;

                    if (InstEmit.Sample1DAs2D)
                    {
                        newType = SamplerType.Texture2D;
                        texOp.InsertSource(coordsCount++, OperandHelper.Const(0));
                    }

                    if (!isImage &&
                        (texOp.Flags & TextureFlags.IntCoords) != 0 &&
                        (texOp.Flags & TextureFlags.LodLevel) == 0)
                    {
                        // IntCoords textures must always have explicit LOD.
                        texOp.SetLodLevelFlag();
                        texOp.InsertSource(coordsCount, OperandHelper.Const(0));
                    }

                    texOp.Type = newType;
                }
            }

            int binding = resourceManager.GetTextureOrImageBinding(
                texOp.Inst,
                texOp.Type,
                texOp.Format,
                texOp.Flags & ~TextureFlags.Bindless,
                cbufSlot,
                cbufOffset);

            texOp.SetBinding(binding);
        }
    }
}
