package com.vex.phonebackup.agent

import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

class AppSnapshotCoordinatorTest {
    @Test
    fun `detects newest crash or anr exit`() {
        val signature = AppSnapshotCoordinator.crashOrAnrSignature(listOf(
            "ApplicationExitInfo #0:",
            "timestamp=2026-08-01 04:10:00.000 pid=42",
            "process=com.example reason=4 (CRASH) subreason=0 status=0",
            "importance=100"
        ))

        assertNotNull(signature)
    }

    @Test
    fun `does not treat ordinary system kill as restore failure`() {
        val signature = AppSnapshotCoordinator.crashOrAnrSignature(listOf(
            "ApplicationExitInfo #0:",
            "timestamp=2026-08-01 04:10:00.000 pid=42",
            "process=com.example reason=13 (OTHER KILLS BY SYSTEM) subreason=6 status=0",
            "importance=400"
        ))

        assertNull(signature)
    }
}
