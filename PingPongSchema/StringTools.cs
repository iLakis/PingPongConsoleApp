using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Utils {
    public class StringTools {

        public static string RemovePrefix(string message, string prefix) {
            if (message.StartsWith(prefix)) {
                return message.Substring(prefix.Length).Trim();
            }
            return message;
        }
    }
}
