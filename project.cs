using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
 
namespace Hello_World
{
    class Program
    {
        static void Main(string[] args)
        {
            // WeldingScoreCalculator 실행
            // - args가 없으면 콘솔에서 angle/speed/distance를 입력받습니다.
            // - 또는 --input CSV / --angle --speed --distance 로 실행할 수 있습니다.
            WeldingScoreCalculator.Run(args);
        }
    }
}
