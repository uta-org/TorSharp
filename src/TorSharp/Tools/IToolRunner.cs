using System.Threading.Tasks;

namespace Knapcode.TorSharp.Tools
{
    internal interface IToolRunner
    {
        Task StartAsync(Tool tool, ITorSharpProxy proxy);

        void Stop();
    }
}