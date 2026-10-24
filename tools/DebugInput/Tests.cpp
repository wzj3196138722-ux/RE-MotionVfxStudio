#include "InputState.hpp"
#include <iostream>
#include <stdexcept>
#include <string>
int main() {
    int checks = 0;
    auto check = [&](bool value) { ++checks; if (!value) throw std::runtime_error("Input check " + std::to_string(checks)); };
    mvl::InputState s; std::array<bool, 256> down{};
    s.configure(true, false, {17,16,18,80}); s.sample(down, true, 1);
    for (int k : s.keys) { down[k] = true; s.sample(down, true, 2); }
    check(s.triggers == 1);
    for (int i=0;i<100;++i) s.sample(down, true, 3);
    check(s.triggers == 1);
    down[80] = false; s.sample(down, true, 4); down[80] = true; s.sample(down, true, 5);
    check(s.triggers == 1); // Must release the entire chord, not just P.
    down.fill(false); s.sample(down, true, 6); for(int k:s.keys) down[k]=true; s.sample(down,true,7);
    check(s.triggers == 2);
    check(!s.allow(80,false,0,8,true)); check(!s.allow(80,false,1,8,true));
    check(s.allow(87,false,0,8,true)); check(s.allow(87,true,0,8,true));
    s.disable(); check(!s.allow(80,false,0,9,true)); check(!s.allow(80,true,0,9,true));
    check(s.allow(80,false,0,10,true)); check(s.allow(80,true,0,10,true));
    check(s.allow(17,false,0,11,true)); s.configure(true,false,{17,16,80}); s.sample({},true,12);
    check(s.allow(17,true,0,13,true)); // Release of a game-owned pre-enable key.
    check(s.allow(80,false,0,3013,true)); check(s.allow(80,true,0,3013,true)); // Lease expired.
    s.sample(down,false,4000); check(s.allow(80,false,0,4001,false)); check(s.triggers==2);
    s.configure(false,true,{17,16,80}); s.sample({},true,5000);
    check(!s.allow(87,false,0,5001,true)); check(!s.allow(87,true,0,5001,true)); check(s.triggers==2);
    s.configure(false,false,{17,16,80}); s.sample({},true,5002); check(s.allow(87,false,0,5003,true));
    for(auto keys:std::vector<std::vector<int>>{{},{17,17},{45},{91},{18,9},{17,18,46},{17,27},{1,2}}) {
        bool rejected=false; try { mvl::validate(keys); } catch(...) { rejected=true; } check(rejected);
    }
    check(mvl::validate({160,162,80}) == std::vector<int>({16,17,80}));
    check(mvl::validate({16,17,18,65,66,67,68,69}).size()==8);
    mvl::InputState poll;
    poll.configure(true,false,{32}); poll.sample({},true,10);
    for(int channel=0;channel<4;++channel) {
        check(poll.mask_polled(32,true,11,true,channel));
        check(!poll.mask_polled(87,true,11,true,channel)); // Movement unaffected.
        check(!poll.mask_polled(1,true,11,true,channel)); // Mouse unaffected.
    }
    poll.disable();
    for(int channel=0;channel<4;++channel) {
        check(poll.mask_polled(32,true,12,true,channel)); // No held-key leak on exit.
        check(poll.mask_polled(32,false,13,true,channel)); // Drain the release.
        check(!poll.mask_polled(32,true,14,true,channel)); // Next press belongs to game.
    }
    poll.configure(true,false,{32}); poll.sample({},true,20);
    check(!poll.allow(32,false,2,21,true,true)); // Peek must not leak later.
    poll.disable();
    check(!poll.allow(32,false,2,22,true));
    check(!poll.allow(32,true,2,23,true,true));
    check(!poll.allow(32,true,2,24,true));
    check(poll.allow(32,false,2,25,true));
    poll.configure(true,false,{32}); poll.sample({},true,30);
    check(!poll.mask_polled(32,true,31,false)); // Other foreground app untouched.
    check(!poll.mask_polled(32,true,3001,true)); // Expired heartbeat fails open.
    std::cout << "PASS: " << checks << " native input checks\n";
}
