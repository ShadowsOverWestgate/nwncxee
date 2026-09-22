layout(location = 1) out uint oVisId;
uint CxVisId(int slot, int prim) { return (uint(slot + 1) << 14) | (uint(prim) & 0x3FFFu); }
