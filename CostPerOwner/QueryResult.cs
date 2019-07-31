using System;
using System.Collections.Generic;
using System.Text;

namespace CostPerOwner
{
    public class QueryResult
    {
        public string id { get; set; }
        public string name { get; set; }
        public string type { get; set; }
        public string location { get; set; }
        public string sku { get; set; }
        public string eTag { get; set; }
        public QueryResultProperties properties { get; set; }
    }
}
