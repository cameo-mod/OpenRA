#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.IO;
using System.Linq;
using NUnit.Framework;
using OpenRA.Network;
using OpenRA.Traits;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class OrderTest
	{
		static byte[] RoundTripOrder(byte[] bytes)
		{
			return Order.Deserialize(null, new BinaryReader(new MemoryStream(bytes))).Serialize();
		}

		[TestCase(TestName = "Order data persists over serialization (empty)")]
		public void SerializeEmpty()
		{
			var o = new Order().Serialize();
			Assert.That(RoundTripOrder(o), Is.EqualTo(o));
		}

		[TestCase(TestName = "Order data persists over serialization (unqueued)")]
		public void SerializeUnqueued()
		{
			var o = new Order("Test", null, false).Serialize();
			Assert.That(RoundTripOrder(o), Is.EqualTo(o));
		}

		[TestCase(TestName = "Order data persists over serialization (queued)")]
		public void SerializeQueued()
		{
			var o = new Order("Test", null, true).Serialize();
			Assert.That(RoundTripOrder(o), Is.EqualTo(o));
		}

		[TestCase(TestName = "Order data persists over serialization (pos target)")]
		public void SerializePos()
		{
			var o = new Order("Test", null, Target.FromPos(new WPos(int.MinValue, 0, int.MaxValue)), false).Serialize();
			Assert.That(RoundTripOrder(o), Is.EqualTo(o));
		}

		[TestCase(TestName = "Order data persists over serialization (invalid target)")]
		public void SerializeInvalid()
		{
			var o = new Order("Test", null, Target.Invalid, false).Serialize();
			Assert.That(RoundTripOrder(o), Is.EqualTo(o));
		}

		[TestCase(TestName = "Order data persists over serialization (extra fields)")]
		public void SerializeExtra()
		{
			var o = new Order("Test", null, Target.Invalid, true)
			{
				TargetString = "TargetString",
				ExtraLocation = new CPos(2047, 2047, 128),
				ExtraData = uint.MaxValue,
				IsImmediate = true,
			}.Serialize();
			Assert.That(RoundTripOrder(o), Is.EqualTo(o));
		}

		// A Fields order whose element count exceeds the bytes left in the frame. The reader stops
		// right after the count, inside the payload. The trailing bytes are a well-formed Handshake,
		// so parsing on from the reader position would yield a spurious "Spurious" order.
		static byte[] FrameWithCorruptCount(short flags, bool terrain)
		{
			var ms = new MemoryStream();
			var w = new BinaryWriter(ms);
			w.Write(new Order("First", null, false).Serialize());

			w.Write((byte)OrderType.Fields);
			w.Write("Bad");
			w.Write(flags);
			if (terrain)
			{
				w.Write((byte)TargetType.Terrain);
				w.Write(0);
				w.Write(0);
				w.Write(0);
				w.Write((short)1000);
			}
			else
				w.Write(1000);

			w.Write((byte)OrderType.Handshake);
			w.Write("Spurious");
			w.Write("x");
			w.Flush();
			return ms.ToArray();
		}

		static string[] ParseFrame(byte[] frame)
		{
			return new OrderPacket(new MemoryStream(frame)).GetOrders(null).Select(o => o.OrderString).ToArray();
		}

		[TestCase((short)0x02, false, TestName = "Corrupt ExtraActors count discards the rest of the frame")]
		[TestCase((short)0x100, false, TestName = "Corrupt Grouped count discards the rest of the frame")]
		[TestCase((short)0x01, true, TestName = "Corrupt terrain position count discards the rest of the frame")]
		public void CorruptCountDiscardsRestOfFrame(short flags, bool terrain)
		{
			Assert.That(ParseFrame(FrameWithCorruptCount(flags, terrain)), Is.EqualTo(new[] { "First" }));
		}

		[TestCase(TestName = "Corrupt count reports malformed and leaves the reader inside the payload")]
		public void CorruptCountReportsMalformed()
		{
			var r = new BinaryReader(new MemoryStream(FrameWithCorruptCount(0x02, false)));
			var first = Order.Deserialize(null, r, out var firstMalformed);
			Assert.That(first?.OrderString, Is.EqualTo("First"));
			Assert.That(firstMalformed, Is.False);

			var bad = Order.Deserialize(null, r, out var badMalformed);
			Assert.That(bad, Is.Null);
			Assert.That(badMalformed, Is.True);
			Assert.That(r.BaseStream.Position, Is.LessThan(r.BaseStream.Length));
		}

		// A valid "First" order, a malformed middle, then bytes that form a valid Handshake.
		static byte[] FrameWithMalformedMiddle(System.Action<BinaryWriter> writeMalformed)
		{
			var ms = new MemoryStream();
			var w = new BinaryWriter(ms);
			w.Write(new Order("First", null, false).Serialize());
			writeMalformed(w);
			w.Write((byte)OrderType.Handshake);
			w.Write("Spurious");
			w.Write("x");
			w.Flush();
			return ms.ToArray();
		}

		[TestCase(TestName = "ReadString throwing mid-payload discards the rest of the frame")]
		public void ReadStringThrowsDiscardsRestOfFrame()
		{
			// Five 7-bit length bytes that all carry the continuation bit: BinaryReader.ReadString
			// throws FormatException after them, with the Handshake bytes still unread.
			var frame = FrameWithMalformedMiddle(w =>
			{
				w.Write((byte)OrderType.Fields);
				w.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
			});

			Assert.That(ParseFrame(frame), Is.EqualTo(new[] { "First" }));

			var r = new BinaryReader(new MemoryStream(frame));
			Order.Deserialize(null, r, out _);
			var bad = Order.Deserialize(null, r, out var malformed);
			Assert.That(bad, Is.Null);
			Assert.That(malformed, Is.True);
		}

		[TestCase(TestName = "Unknown order type discards the rest of the frame")]
		public void UnknownTypeDiscardsRestOfFrame()
		{
			// 0x42 is not an OrderType: its length is unknown, so nothing after it can be framed.
			var frame = FrameWithMalformedMiddle(w => w.Write((byte)0x42));
			Assert.That(ParseFrame(frame), Is.EqualTo(new[] { "First" }));
		}

		[TestCase(TestName = "Well-formed frames still yield every order")]
		public void WellFormedFrameYieldsAll()
		{
			var ms = new MemoryStream();
			foreach (var name in new[] { "A", "B", "C" })
				ms.Write(new Order(name, null, Target.Invalid, false) { ExtraData = 7 }.Serialize());

			Assert.That(ParseFrame(ms.ToArray()), Is.EqualTo(new[] { "A", "B", "C" }));
		}
	}
}
