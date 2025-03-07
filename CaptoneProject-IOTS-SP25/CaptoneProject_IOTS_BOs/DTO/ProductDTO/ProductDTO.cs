﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CaptoneProject_IOTS_BOs.DTO.ProductDTO
{
    public class GeneralProductDTO
    {
        public string? Name { set; get; }
        public string? Summary { set; get; }
        public decimal Price { set; get; }
        public int? CreatedBy { set; get; }
        public string? CreatedByStore { set; get; }

        public string? ImageUrl { set; get; }
    }
}
