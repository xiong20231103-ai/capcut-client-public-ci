using System;
using System.Collections.Generic;

namespace CapCutBatchClient
{
    internal static class CapCutSessionOwnership
    {
        // A process that was already present before this automation session is
        // never owned by the batch client.  A CapCut process that appears after
        // the recorded launch time belongs to this session, even when its
        // window is created after the first UI wait has timed out.
        public static bool IsOwnedCandidate(int processId, DateTime processStartedUtc,
            ISet<int> preLaunchProcessIds, DateTime sessionStartedUtc)
        {
            if (processId <= 0 || sessionStartedUtc == DateTime.MinValue)
                return false;
            if (preLaunchProcessIds != null &&
                preLaunchProcessIds.Contains(processId))
                return false;
            return processStartedUtc >= sessionStartedUtc.AddSeconds(-5);
        }
    }
}
