using System;
using System.Collections.Generic;
using System.Text;

namespace CostPerOwner
{
    public class QueryResultProperties
    {
        public string nextLink { get; set; }
        public List<QueryResultColumn> columns { get; set; }
        public List<List<object>> rows { get; set; }
       
    }
}
