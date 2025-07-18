﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UndertaleModLib.Models;
using UndertaleModLib.Resources.Languages;
using static UndertaleModLib.UndertaleReader;

namespace UndertaleModLib
{
    public abstract class UndertaleChunk
    {
        public abstract string Name { get; }
        public uint Length { get; internal set; }

        internal abstract void SerializeChunk(UndertaleWriter writer);
        internal abstract void UnserializeChunk(UndertaleReader reader);
        internal abstract uint UnserializeObjectCount(UndertaleReader reader);

        public void Serialize(UndertaleWriter writer)
        {
            try
            {
                Util.DebugUtil.Assert(Name != null);
                writer.Write(Name.ToCharArray());
                var lenWriter = writer.WriteLengthHere();

                writer.SubmitMessage(Resource.Msg_WritingChunk + Name);
                lenWriter.FromHere();
                SerializeChunk(writer);
                
                if (Name != "FORM" && Name != writer.LastChunkName)
                {
                    UndertaleGeneralInfo generalInfo = Name == "GEN8" ? ((UndertaleChunkGEN8)this).Object : writer.undertaleData?.GeneralInfo;
                    // These versions introduced new padding
                    // all chunks now start on 16-byte boundaries
                    // (but the padding is included with length of previous chunk)
                    // TODO: what about the debug data??
                    if (generalInfo != null && (generalInfo.Major >= 2 || (generalInfo.Major == 1 && generalInfo.Build >= 9999)))
                    {
                        int e = writer.undertaleData.PaddingAlignException;
                        uint pad = (e == -1 ? 16 : (uint)e);
                        while (writer.Position % pad != 0)
                        {
                            writer.Write((byte)0);
                        }
                    }
                }

                Length = lenWriter.ToHere();
            }
            catch (UndertaleSerializationException e)
            {
                throw new UndertaleSerializationException(e.Message + " in chunk " + Name, e);
            }
            catch (Exception e)
            {
                throw new UndertaleSerializationException(e.Message + "\nat " + writer.Position.ToString("X8") + " while reading chunk " + Name, e);
            }
        }

        public static UndertaleChunk Unserialize(UndertaleReader reader)
        {
            string name = "(unknown)";
            try
            {
                // Read name and length
                name = reader.ReadChars(4);
                uint length = reader.ReadUInt32();

                // Find chunk instance, or create one if not already created (when errors occur during object counting)
                if (!reader.undertaleData.FORM.Chunks.TryGetValue(name, out UndertaleChunk chunk))
                {
                    if (!UndertaleChunkFORM.ChunkConstructors.TryGetValue(name, out Func<UndertaleChunk> instantiator))
                    {
                        throw new IOException($"Unknown chunk \"{name}\"");
                    }
                    chunk = instantiator();
                    reader.undertaleData.FORM.Chunks[name] = chunk;
                }
                Util.DebugUtil.Assert(chunk.Name == name,
                                      $"Chunk name mismatch: expected \"{name}\", got \"{chunk.Name}\".");
                chunk.Length = length;

                // Read chunk contents
                reader.SubmitMessage(Resource.Msg_ReadingChunk + chunk.Name);
                EnsureLengthOperation lenReader = reader.EnsureLengthFromHere(chunk.Length);
                reader.CopyChunkToBuffer(length);
                chunk.UnserializeChunk(reader);

                // Process padding
                reader.SwitchReaderType(false);
                if (name != "FORM" && name != reader.LastChunkName)
                {
                    UndertaleGeneralInfo generalInfo = name == "GEN8" ? ((UndertaleChunkGEN8)chunk).Object : reader.undertaleData.GeneralInfo;

                    // These versions introduced new padding
                    // all chunks now start on 16-byte boundaries
                    // (but the padding is included with length of previous chunk)
                    if (generalInfo.Major >= 2 || (generalInfo.Major == 1 && generalInfo.Build >= 9999))
                    {
                        int e = reader.undertaleData.PaddingAlignException;
                        uint pad = (e == -1 ? 16 : (uint)e);
                        while (reader.Position % pad != 0)
                        {
                            if (reader.ReadByte() != 0)
                            {
                                reader.Position -= 1;
                                if (reader.Position % 4 == 0)
                                    reader.undertaleData.PaddingAlignException = 4;
                                else
                                    reader.undertaleData.PaddingAlignException = 1;
                                break;
                            }
                        }
                    }
                }

                // Ensure full length was read
                lenReader.ToHere();

                return chunk;
            }
            catch (UndertaleSerializationException e)
            {
                throw new UndertaleSerializationException(e.Message + " in chunk " + name, e);
            }
            catch (Exception e)
            {
                throw new UndertaleSerializationException(e.Message + "\nat " + reader.AbsPosition.ToString("X8") + " while reading chunk " + name, e);
            }
        }
        public static (uint, UndertaleChunk) CountChunkChildObjects(UndertaleReader reader)
        {
            string name = "(unknown)";
            try
            {
                // Read name and length
                name = reader.ReadChars(4);
                uint length = reader.ReadUInt32();

                // Create chunk instance
                if (!UndertaleChunkFORM.ChunkConstructors.TryGetValue(name, out Func<UndertaleChunk> instantiator))
                {
                    throw new IOException($"Unknown chunk \"{name}\"");
                }
                UndertaleChunk chunk = instantiator();
                Util.DebugUtil.Assert(chunk.Name == name,
                                      $"Chunk name mismatch: expected \"{name}\", got \"{chunk.Name}\".");
                chunk.Length = length;

                // Count objects in chunk
                long chunkStart = reader.Position;
                reader.SubmitMessage("Counting objects of chunk " + chunk.Name);
                reader.CopyChunkToBuffer(length);
                uint count = chunk.UnserializeObjectCount(reader);

                // Advance beyond chunk length (parts of the chunk may have been skipped)
                reader.SwitchReaderType(false);
                reader.Position = chunkStart + chunk.Length;

                return (count, chunk);
            }
            catch (UndertaleSerializationException e)
            {
                throw new UndertaleSerializationException(e.Message + " in chunk " + name, e);
            }
            catch (Exception e)
            {
                throw new UndertaleSerializationException(e.Message + "\nat " + reader.AbsPosition.ToString("X8") + " while counting objects of chunk " + name, e);
            }
        }
    }

    public interface IUndertaleSingleChunk
    {
        UndertaleObject GetObject();
    }
    public interface IUndertaleListChunk
    {
        IList GetList();
        void GenerateIndexDict();
        void ClearIndexDict();
    }
    public interface IUndertaleSimpleListChunk
    {
        IList GetList();
    }


    public abstract class UndertaleSingleChunk<T> : UndertaleChunk, IUndertaleSingleChunk where T : UndertaleObject, new()
    {
        public T Object;

        internal override void SerializeChunk(UndertaleWriter writer)
        {
            writer.WriteUndertaleObject(Object);
        }

        internal override void UnserializeChunk(UndertaleReader reader)
        {
            Object = reader.ReadUndertaleObject<T>();
        }

        internal override uint UnserializeObjectCount(UndertaleReader reader)
        {
            uint count = 1;

            count += reader.GetChildObjectCount<T>();

            return count;
        }

        public UndertaleObject GetObject() => Object;

        public override string ToString()
        {
            return Object.ToString();
        }
    }

    public abstract class UndertaleListChunk<T> : UndertaleChunk, IUndertaleListChunk where T : UndertaleObject, new()
    {
        public UndertalePointerList<T> List = new UndertalePointerList<T>();
        public Dictionary<T, int> IndexDict;

        internal override void SerializeChunk(UndertaleWriter writer)
        {
            List.Serialize(writer);
        }

        internal override void UnserializeChunk(UndertaleReader reader)
        {
            List.Unserialize(reader);
        }

        internal override uint UnserializeObjectCount(UndertaleReader reader)
        {
            return UndertalePointerList<T>.UnserializeChildObjectCount(reader);
        }

        public IList GetList() => List;
        public void GenerateIndexDict()
        {
            IndexDict = new(List.Count);
            for (int i = 0; i < List.Count; i++)
            {
                if (List[i] is not null)
                    IndexDict[List[i]] = i;
            }
        }
        public void ClearIndexDict()
        {
            IndexDict = null;
        }
    }

    public abstract class UndertaleAlignUpdatedListChunk<T> : UndertaleListChunk<T> where T : UndertaleObject, new()
    {
        public bool Align = true;
        protected int Alignment = 4;

        internal override void SerializeChunk(UndertaleWriter writer)
        {
            writer.Write(List.Count);
            uint baseAddr = writer.Position;
            for (int i = 0; i < List.Count; i++)
                writer.Write(0);
            for (int i = 0; i < List.Count; i++)
            {
                if (Align)
                {
                    while (writer.Position % Alignment != 0)
                        writer.Write((byte)0);
                }
                if (List[i] is null)
                    continue;
                uint returnTo = writer.Position;
                writer.Position = baseAddr + ((uint)i * 4);
                writer.Write(returnTo);
                writer.Position = returnTo;
                writer.WriteUndertaleObject(List[i]);
            }
        }

        internal override void UnserializeChunk(UndertaleReader reader)
        {
            uint count = reader.ReadUInt32();
            List.SetCapacity(count);
            uint realCount = count;
            BitArray gm2024_11_WhatToSkip = null;
            if (reader.undertaleData.IsVersionAtLeast(2024, 11) && count > 0)
                gm2024_11_WhatToSkip = new((int)count, false);

            for (int i = 0; i < count; i++)
            {
                uint readValue = reader.ReadUInt32();
                Align &= (readValue % Alignment == 0);
                if (readValue != 0) continue;

                if (reader.undertaleData.IsVersionAtLeast(2024, 11) && gm2024_11_WhatToSkip is not null)
                {
                    // This is "normal" and is likely a object removed by GMAC.
                    gm2024_11_WhatToSkip.Set(i, true);
                    continue;
                }

                realCount--;
            }

            for (int i = 0; i < realCount; i++)
            {
                if (gm2024_11_WhatToSkip is not null && gm2024_11_WhatToSkip.Get(i))
                {
                    List.InternalAdd(default);
                    continue;
                }

                if (Align)
                {
                    while (reader.AbsPosition % Alignment != 0)
                        if (reader.ReadByte() != 0)
                            throw new IOException("AlignUpdatedListChunk padding error");
                }
                List.InternalAdd(reader.ReadUndertaleObject<T>());
            }
        }

        internal override uint UnserializeObjectCount(UndertaleReader reader)
        {
            uint claimedCount = reader.ReadUInt32(), count = claimedCount;
            if (count == 0)
                return 0;

            for (int i = 0; i < claimedCount; i++)
            {
                uint readValue = reader.ReadUInt32();
                Align &= (readValue % Alignment == 0);
                if (readValue != 0) continue;

                if (reader.undertaleData.GeneralInfo.BytecodeVersion >= 13 && !reader.undertaleData.IsVersionAtLeast(2024, 11))
                {
                    reader.SubmitWarning("Zero values in an AlignUpdatedListChunk encountered on potential pre-2024.11 Bytecode 13+!");
                }
                count--;
            }
            if (count == 0)
                return 0;

            Type t = typeof(T);
            if (t != typeof(UndertaleBackground) && t != typeof(UndertaleString))
                throw new InvalidOperationException(
                    "\"UndertaleAlignUpdatedListChunk<T>\" supports the count unserialization only for backgrounds and strings.");

            return count;
        }
    }

    public abstract class UndertaleSimpleListChunk<T> : UndertaleChunk, IUndertaleSimpleListChunk where T : UndertaleObject, new()
    {
        public UndertaleSimpleList<T> List = new UndertaleSimpleList<T>();

        internal override void SerializeChunk(UndertaleWriter writer)
        {
            List.Serialize(writer);
        }

        internal override void UnserializeChunk(UndertaleReader reader)
        {
            List.Unserialize(reader);
        }

        internal override uint UnserializeObjectCount(UndertaleReader reader)
        {
            return reader.ReadUInt32();
        }

        public IList GetList() => List;
    }

    public abstract class UndertaleEmptyChunk : UndertaleChunk
    {
        internal override void SerializeChunk(UndertaleWriter writer)
        {
        }

        internal override void UnserializeChunk(UndertaleReader reader)
        {
        }

        internal override uint UnserializeObjectCount(UndertaleReader reader) => 0;
    }

    public abstract class UndertaleUnsupportedChunk : UndertaleChunk
    {
        public byte[] RawData;
        internal override void SerializeChunk(UndertaleWriter writer)
        {
            writer.Write(RawData);
        }

        internal override void UnserializeChunk(UndertaleReader reader)
        {
            RawData = reader.ReadBytes((int)Length);
        }

        internal override uint UnserializeObjectCount(UndertaleReader reader) => 0;
    }
}
