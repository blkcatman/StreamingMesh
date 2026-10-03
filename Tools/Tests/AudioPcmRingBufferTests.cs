using System;
using System.IO;
using System.Threading;
using StreamingMesh.Core.Threading;

// Standalone tests: compile with Pcm16RingBuffer.cs; no Unity or test framework dependency.
static class AudioPcmRingBufferTests
{
  static int passed;
  static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
  static byte[] Expected(float[] input, int channels)
  {
    var result = new byte[input.Length / channels * 4];
    for (int i = 0; i < input.Length / channels; i++)
      for (int c = 0; c < 2; c++)
      {
        short value = (short)Math.Round(Math.Max(-1d, Math.Min(1d, input[i*channels + Math.Min(c,channels-1)]))*32767d);
        result[i*4+c*2]=(byte)value; result[i*4+c*2+1]=(byte)(value>>8);
      }
    return result;
  }
  static void Equal(byte[] actual, byte[] expected)
  {
    Check(actual.Length==expected.Length,"PCM length");
    for(int i=0;i<actual.Length;i++)Check(actual[i]==expected[i],"PCM byte at "+i);
  }
  static void Drain(Pcm16RingBuffer ring, Stream output) { while(ring.WriteAvailable(output)!=0){} }
  static void Conversion()
  {
    foreach(int channels in new[]{1,2,3})
    {
      var input=new float[channels*9];
      for(int i=0;i<input.Length;i++) input[i]=(i%9-4)*0.4f;
      var ring=new Pcm16RingBuffer(new byte[64]);
      Check(ring.TryWrite(input,channels,12.345),"conversion accepted");
      Check(ring.TryGetFirstDspTime(out double dsp)&&dsp==12.345,"first DSP time");
      using(var output=new MemoryStream()){Drain(ring,output);Equal(output.ToArray(),Expected(input,channels));}
      ring.Close();Check(ring.IsDrained,"conversion drained");
    }
  }
  static void WrapAndOverflow()
  {
    var ring=new Pcm16RingBuffer(new byte[12]); var input=new[]{0.5f,-0.5f};
    using(var output=new MemoryStream())
    {
      for(int i=0;i<100;i++){Check(ring.TryWrite(input,1,i),"wrap accepted");Drain(ring,output);}
      byte[] expected=Expected(input,1), actual=output.ToArray();
      for(int i=0;i<actual.Length;i++)Check(actual[i]==expected[i%expected.Length],"wrapped bytes");
      Check(ring.TryWrite(input,1,100),"fill");Check(!ring.TryWrite(input,1,101),"overflow rejects whole block");
      Check(!ring.TryWrite(new float[3],2,102),"incomplete frame rejected");
      ring.Close();Check(!ring.IsDrained,"close retains queued data");Check(!ring.TryWrite(input,1,103),"closed producer");
      Drain(ring,output);Check(ring.IsDrained,"close drains");
      Check(ring.TryGetFirstDspTime(out double first)&&first==0,"first timestamp retained");
      var next=new Pcm16RingBuffer(ring.Storage);
      Check(!next.TryGetFirstDspTime(out _),"fresh session timestamp");
      Check(!ring.TryWrite(input,1,200),"old wrapper remains closed after storage reuse");
      Check(next.TryWrite(input,1,201),"new wrapper accepts reused storage");
    }
  }
  sealed class BlockingStream : MemoryStream
  {
    internal readonly ManualResetEventSlim Entered=new ManualResetEventSlim(), Release=new ManualResetEventSlim();
    public override void Write(byte[] buffer,int offset,int count)
    {Entered.Set();Check(Release.Wait(5000),"blocked consumer released");base.Write(buffer,offset,count);}
  }
  static void ConsumerOwnership()
  {
    var ring=new Pcm16RingBuffer(new byte[8]); var input=new[]{0.5f,-0.5f};
    using(var output=new BlockingStream())
    {
      Check(ring.TryWrite(input,1,1),"initial block");
      Exception error=null;
      var writer=new Thread(()=>{try{ring.WriteAvailable(output);}catch(Exception e){error=e;}});
      writer.Start();Check(output.Entered.Wait(5000),"consumer entered Write");
      Check(!ring.TryWrite(new[]{1f,1f},1,2),"blocked range cannot be overwritten");
      ring.Close();Check(!ring.IsDrained,"blocked Write owns its range after close");
      output.Release.Set();Check(writer.Join(5000),"consumer exited");if(error!=null)throw error;
      Check(ring.IsDrained,"release after Write");Equal(output.ToArray(),Expected(input,1));
    }
  }
  static void ConcurrentOrder()
  {
    const int blocks=20000;
    var ring=new Pcm16RingBuffer(new byte[256]);var inputs=new float[7][];
    for(int i=0;i<7;i++)inputs[i]=new float[(i+1)*2];
    using(var output=new MemoryStream(600000))
    {
      Exception error=null;long deadline=Environment.TickCount64+10000;
      var writer=new Thread(()=>{try{while(!ring.IsDrained){if(ring.WriteAvailable(output)==0)Thread.Yield();Check(Environment.TickCount64<deadline,"consumer timeout");}}catch(Exception e){error=e;}});
      writer.Start();
      using(var expected=new MemoryStream(600000))
      {
        for(int i=0;i<blocks;i++)
        {
          var block=inputs[i%7];for(int j=0;j<block.Length;j++)block[j]=((i+j)%32-16)/16f;
          while(!ring.TryWrite(block,2,i)){Check(error==null&&Environment.TickCount64<deadline,"producer timeout");Thread.Yield();}
          var bytes=Expected(block,2);expected.Write(bytes,0,bytes.Length);
        }
        ring.Close();Check(writer.Join(5000),"concurrent consumer exits");if(error!=null)throw error;
        Equal(output.ToArray(),expected.ToArray());
      }
    }
  }
  static void WarmAllocations()
  {
    var ring=new Pcm16RingBuffer(new byte[8192]);var block=new float[2048];
    for(int i=0;i<100;i++){ring.TryWrite(block,2,i);Drain(ring,Stream.Null);}
    long before=GC.GetAllocatedBytesForCurrentThread();
    var positive=new byte[1000000];GC.KeepAlive(positive);
    Check(GC.GetAllocatedBytesForCurrentThread()-before>=1000000,"allocation positive control");
    before=GC.GetAllocatedBytesForCurrentThread();
    for(int i=0;i<10000;i++){if(!ring.TryWrite(block,2,i))throw new Exception("unexpected full ring");Drain(ring,Stream.Null);}
    long bytes=GC.GetAllocatedBytesForCurrentThread()-before;
    Check(bytes==0,"warm PCM allocation bytes: "+bytes);
    Console.WriteLine("Warm PCM producer/consumer: 10000 blocks, 0 new managed bytes");
  }
  static int Main()
  {
    try{foreach(var test in new Action[]{Conversion,WrapAndOverflow,ConsumerOwnership,ConcurrentOrder,WarmAllocations}){test();passed++;Console.WriteLine("PASS "+test.Method.Name);}return 0;}
    catch(Exception error){Console.Error.WriteLine(error);return 1;}
  }
}
