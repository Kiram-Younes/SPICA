﻿using SPICA.Formats.H3D;
using SPICA.Serialization.Attributes;

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace SPICA.Serialization
{
    class BinarySerializer
    {
        public Stream BaseStream;
        public H3DRelocator Relocator;

        public BinaryWriter Writer;

        public delegate void OnSerialize(BinarySerializer Serializer, object Value);

        public struct RefValue
        {
            public OnSerialize Serialize;
            public FieldInfo Info;
            public object Value;
            public long Position;
            public bool HasLength;
            public bool HasTwoPtr;
        }

        public struct ObjectInfo
        {
            public uint Position;
            public int Length;

            public void SetEnd(long Position)
            {
                Length = (int)(Position - this.Position);
            }
        }

        public class Section
        {
            public List<RefValue> Values;
            public ObjectInfo Info;

            public Section()
            {
                Values = new List<RefValue>();
            }
        }

        public Section Contents;

        public Section Strings;
        public Section Commands;

        public Section RawDataTex;
        public Section RawDataVtx;
        public Section RawExtTex;
        public Section RawExtVtx;

        public Dictionary<object, ObjectInfo> ObjPointers;

        public List<long> Pointers;

        private bool HasBuffered = false;
        private uint BufferedUInt = 0;
        private uint BufferedShift = 0;

        public BinarySerializer(Stream BaseStream, H3DRelocator Relocator = null)
        {
            this.BaseStream = BaseStream;
            this.Relocator = Relocator;

            Writer = new BinaryWriter(BaseStream);

            Contents = new Section();

            Strings = new Section();
            Commands = new Section();

            RawDataTex = new Section();
            RawDataVtx = new Section();
            RawExtTex = new Section();
            RawExtVtx = new Section();

            ObjPointers = new Dictionary<object, ObjectInfo>();

            Pointers = new List<long>();
        }

        public void Serialize(object Value)
        {
            Contents.Info.Position = (uint)BaseStream.Position;

            WriteValue(Value);

            Contents.Info.SetEnd(BaseStream.Position);

            Strings.Values.RemoveAll(x => x.Value == null);
            Strings.Values.Sort(CompareString);

            WriteSection(Strings, 0x10);
            WriteSection(Commands, 0x80);

            WriteSection(RawDataTex, 0x80);
            WriteSection(RawDataVtx, 0x80);
            WriteSection(RawExtTex, 0x80);
            WriteSection(RawExtVtx, 0x80);
        }

        private static int CompareString(RefValue x, RefValue y)
        {
            string LHS = (string)x.Value;
            string RHS = (string)y.Value;

            for (int Index = 0; Index < Math.Min(LHS.Length, RHS.Length); Index++)
            {
                byte L = (byte)LHS[Index];
                byte R = (byte)RHS[Index];

                if (L != R) return L < R ? -1 : 1;
            }

            if (LHS.Length == RHS.Length)
            {
                return 0;
            }
            else if (LHS.Length < RHS.Length)
            {
                return -1;
            }
            else
            {
                return 1;
            }
        }

        private void WriteSection(Section Section, int Align)
        {
            Section.Info.Position = (uint)BaseStream.Position;

            while (Section.Values.Count > 0)
            {
                WriteValue(Section.Values[0]);
                Section.Values.RemoveAt(0);
            }

            Section.Info.SetEnd(BaseStream.Position);

            while ((BaseStream.Position % Align) != 0) BaseStream.WriteByte(0);
        }

        private void WriteValue(object Value, FieldInfo Info = null, bool IsElem = false)
        {
            Type Type = Value.GetType();
            long Position = BaseStream.Position;

            if (Type.IsPrimitive || Type.IsEnum)
            {
                switch (Type.GetTypeCode(Type))
                {
                    case TypeCode.UInt64: Writer.Write((ulong)Value); break;
                    case TypeCode.UInt32: Writer.Write((uint)Value); break;
                    case TypeCode.UInt16: Writer.Write((ushort)Value); break;
                    case TypeCode.Byte: Writer.Write((byte)Value); break;
                    case TypeCode.Int64: Writer.Write((long)Value); break;
                    case TypeCode.Int32: Writer.Write((int)Value); break;
                    case TypeCode.Int16: Writer.Write((short)Value); break;
                    case TypeCode.SByte: Writer.Write((sbyte)Value); break;
                    case TypeCode.Single: Writer.Write((float)Value); break;
                    case TypeCode.Double: Writer.Write((double)Value); break;
                    case TypeCode.Boolean:
                        HasBuffered = true;
                        BufferedUInt <<= 1;
                        BufferedUInt |= (uint)(((bool)Value) ? 1 : 0);

                        if (++BufferedShift == 32 || !IsElem) WriteBool();

                        break;
                }
            }
            else if (Value is IList)
            {
                WriteList((IList)Value, Info);
            }
            else if (Value is string)
            {
                Writer.Write(Encoding.ASCII.GetBytes((string)Value + '\0'));
            }
            else
            {
                WriteObject(Value, IsElem);
            }

            //Avoid writing the same Object more than once
            if (!(Type.IsPrimitive || Type.IsEnum)) AddObjPointer(Value, Position);
        }

        private void AddObjPointer(object Value, long Position)
        {
            if (!ObjPointers.ContainsKey(Value))
            {
                ObjPointers.Add(Value, new ObjectInfo
                {
                    Position = (uint)Position,
                    Length = (int)(BaseStream.Position - Position)
                });
            }
        }

        private void WriteBool()
        {
            Writer.Write(BufferedUInt);

            BufferedShift = 0;
            HasBuffered = false;
        }

        private void WriteList(IList List, FieldInfo Info)
        {
            bool Pointers = Info != null && Info.IsDefined(typeof(PointersAttribute));

            foreach (object Value in List)
            {
                if (Pointers)
                {
                    Contents.Values.Add(new RefValue
                    {
                        Value = Value,
                        Position = BaseStream.Position
                    });

                    Skip(4);
                }
                else
                {
                    WriteValue(Value, null, true);
                }
            }

            if (HasBuffered) WriteBool();
        }

        private void WriteValue(RefValue Reference)
        {
            object Value = Reference.Value;

            if (Value != null)
            {
                FieldInfo Info = Reference.Info;
                ObjectInfo OInfo = GetObjInfo(Value, Info);
                long Position = BaseStream.Position;
                bool Range = Info != null && Info.IsDefined(typeof(RangeAttribute));

                Reference.Serialize?.Invoke(this, Value);

                if (Reference.Position != -1)
                {
                    BaseStream.Seek(Reference.Position, SeekOrigin.Begin);

                    Pointers.Add(BaseStream.Position);
                    Writer.Write(OInfo.Position);

                    if (Reference.HasLength && !Range) Writer.Write(((IList)Value).Count);
                    if (Reference.HasTwoPtr)
                    {
                        Pointers.Add(BaseStream.Position);
                        Writer.Write(OInfo.Position);
                    }

                    BaseStream.Seek(Position, SeekOrigin.Begin);
                }

                if (OInfo.Position == Position)
                {
                    AddObjPointer(Value, Position);
                    WriteValue(Value, Info);
                }

                if (Range)
                {
                    Position = BaseStream.Position;

                    BaseStream.Seek(Reference.Position + 4, SeekOrigin.Begin);

                    Pointers.Add(BaseStream.Position);
                    Writer.Write((uint)(OInfo.Length != 0 ? OInfo.Length : Position));

                    BaseStream.Seek(Position, SeekOrigin.Begin);
                }
            }
        }

        private ObjectInfo GetObjInfo(object Value, FieldInfo Info)
        {
            ObjectInfo Output = new ObjectInfo
            {
                Position = (uint)BaseStream.Position,
                Length = 0
            };

            if (ObjPointers.ContainsKey(Value))
            {
                Output = ObjPointers[Value];
            }
            else if (Value is IList)
            {
                uint SPos = 0;
                int EPos = 0;
                int Matches = 0;

                foreach (object Elem in ((IList)Value))
                {
                    if (ObjPointers.ContainsKey(Elem) && (ObjPointers[Elem].Position == EPos || EPos == 0))
                    {
                        if (Matches++ == 0) EPos = (int)(SPos = ObjPointers[Elem].Position);

                        EPos += ObjPointers[Elem].Length;
                    }
                    else
                    {
                        break;
                    }
                }

                if (Matches > 0 && Matches == ((IList)Value).Count)
                {
                    Output.Position = SPos;
                    Output.Length = EPos;
                }
            }

            return Output;
        }

        public void WriteObject(object Value, bool IsElem = false)
        {
            Type ValueType = Value.GetType();

            int Index = Contents.Values.Count;

            if (Value is ICustomSerialization)
            {
                if (((ICustomSerialization)Value).Serialize(this)) return;
            }

            foreach (FieldInfo Info in ValueType.GetFields())
            {
                if (!Info.IsDefined(typeof(NonSerializedAttribute)))
                {
                    Type Type = Info.FieldType;

                    bool Inline;

                    Inline = Info.IsDefined(typeof(InlineAttribute));
                    Inline |= Type.IsDefined(typeof(InlineAttribute));

                    if (Type.IsValueType || Type.IsEnum || Inline)
                    {
                        WriteValue(Info.GetValue(Value), Info);
                    }
                    else
                    {
                        bool IsList = typeof(IList).IsAssignableFrom(Type);
                        bool HasLength = !Info.IsDefined(typeof(FixedLengthAttribute)) && IsList;
                        bool HasTwoPtr = Info.IsDefined(typeof(RepeatPointerAttribute));

                        RefValue Ref = new RefValue
                        {
                            Info = Info,
                            Value = Info.GetValue(Value),
                            Position = BaseStream.Position,
                            HasLength = HasLength,
                            HasTwoPtr = HasTwoPtr
                        };

                        if (Value is ICustomSerializeCmd && Type == typeof(uint[]))
                        {
                            Ref.Serialize = ((ICustomSerializeCmd)Value).SerializeCmd;
                        }

                        if (Type == typeof(string))
                        {
                            Strings.Values.Add(Ref);
                        }
                        else if (Type == typeof(uint[]))
                        {
                            Commands.Values.Add(Ref);
                        }
                        else
                        {
                            Contents.Values.Add(Ref);
                        }

                        Skip((HasLength ? 8 : 4) + (HasTwoPtr ? 4 : 0));
                    }
                }
            }

            if (!IsElem && (ValueType.IsClass && !ValueType.IsDefined(typeof(InlineAttribute))))
            {
                while (Index < Contents.Values.Count)
                {
                    WriteValue(Contents.Values[Index]);
                    Contents.Values.RemoveAt(Index);
                }
            }
        }

        public void Skip(int Bytes)
        {
            while (Bytes-- > 0) BaseStream.WriteByte(0);
        }
    }
}
