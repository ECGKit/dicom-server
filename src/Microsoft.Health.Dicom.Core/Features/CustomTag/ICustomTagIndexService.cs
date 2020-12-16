﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dicom;
using Microsoft.Health.Dicom.Core.Features.Model;

namespace Microsoft.Health.Dicom.Core.Features.CustomTag
{
    public interface ICustomTagIndexService
    {
        Task AddCustomTagIndexes(Dictionary<long, DicomItem> customTagIndexes, InstanceIdentifier instanceIdentifier, CancellationToken cancellationToken = default);
    }
}
