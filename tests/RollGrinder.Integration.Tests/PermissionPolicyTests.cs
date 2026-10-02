using System;
using System.Linq;
using FluentAssertions;
using RollGrinder.Contracts.Dtos;
using RollGrinder.Services.Session;
using Xunit;

namespace RollGrinder.Integration.Tests;

/// <summary>权限细分（修改稿问题 Q9）：哪项权限归哪一级只在权限表里定。</summary>
public sealed class PermissionPolicyTests
{
    [Fact]
    public void Operators_run_the_machine_and_build_jobs_but_do_not_edit_the_libraries()
    {
        PermissionPolicy.GrantedTo(UserRole.Operator).Should().BeEquivalentTo(new[]
        {
            Permission.RunMachine, Permission.EditJobs,
        });
    }

    [Fact]
    public void Administrators_add_profiles_programs_calibration_and_accounts()
    {
        PermissionPolicy.GrantedTo(UserRole.Administrator).Should().BeEquivalentTo(new[]
        {
            Permission.RunMachine, Permission.EditJobs, Permission.EditProfiles, Permission.EditPrograms,
            Permission.EditCalibration, Permission.ManageUsers, Permission.EditRollPlans,
        });
    }

    [Fact]
    public void Only_the_manufacturer_edits_compensation_machine_config_and_tag_map()
    {
        foreach (Permission permission in new[] { Permission.EditCompensation, Permission.EditMachineConfig, Permission.EditTagMap })
        {
            PermissionPolicy.MinimumRole(permission).Should().Be(UserRole.Manufacturer);
        }

        PermissionPolicy.GrantedTo(UserRole.Manufacturer).Should().BeEquivalentTo(Enum.GetValues<Permission>());
    }

    [Fact]
    public void Every_permission_is_in_the_table()
    {
        Enum.GetValues<Permission>().Select(PermissionPolicy.MinimumRole).Should().HaveCount(Enum.GetValues<Permission>().Length);
    }

    [Fact]
    public void Signed_out_there_is_no_permission_at_all()
    {
        var session = new UserSession();
        Enum.GetValues<Permission>().Should().OnlyContain(permission => !session.Can(permission));

        session.SignIn(new UserAccount("li", UserRole.Operator, false, DateTimeOffset.UnixEpoch));
        session.Can(Permission.RunMachine).Should().BeTrue();
        session.Can(Permission.EditPrograms).Should().BeFalse();

        session.SignOut();
        session.Can(Permission.RunMachine).Should().BeFalse();
    }
}
